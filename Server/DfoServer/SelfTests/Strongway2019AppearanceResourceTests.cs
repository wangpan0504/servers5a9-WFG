using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 定向真实资源回归；边框历史正控不代表当前墙钟可使用。
internal static class Strongway2019AppearanceResourceTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        int[] tokens = [490013039, 490013040, 490013041];
        int[] skins = [430000134, 430000135, 430000136];
        long frameExpiry = new DateTimeOffset(2020, 11, 28, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(resources.Skins.HasCutscenes && resources.Skins.HasPartyFrames,
            "真实插图与边框默认SKN30000/20000均在册");
        for (int index = 0; index < tokens.Length; index++)
        {
            var definition = resources.Skins.Token(tokens[index]);
            That(resources.Items.Get(tokens[index]) is { Kind: ItemKind.Consumable, StackableType: "[waste] 0", StackLimit: 1,
                    MinimumLevel: 1, Jobs: ushort.MaxValue, UseForbidden: false } &&
                resources.Items.StackableAction(tokens[index]) is { ClientType: 199, Label: "[add skin storage]" } &&
                definition is { Type: 1, AccountWide: true, Expiration: { AbsoluteTime: 0, Days: 0 } } &&
                definition.SkinIds.SequenceEqual(new[] { skins[index] }) && definition.Places.SequenceEqual(new[] { "[village]" }),
                "真实插图券保留精确SKN、账号永久归属及城镇资格：" + tokens[index]);
        }
        var frame = resources.Skins.Token(490013318);
        That(resources.Items.Get(490013318) is { Kind: ItemKind.Consumable, StackableType: "[waste] 0", StackLimit: 1 } &&
            resources.Items.StackableAction(490013318) is { ClientType: 199 } &&
            frame is { Type: 0, AccountWide: false, SkinId: 420000004, Expiration.Days: 0 } &&
            frame.SkinIds.SequenceEqual(new[] { 420000004 }) && frame.Expiration.AbsoluteTime == frameExpiry &&
            frame.Places.SequenceEqual(new[] { "[village]" }) && frameExpiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "边框按显式character和2020固定日期，不按说明文字改为账号或永久");
        var box = resources.Boosters.Get(490013316);
        That(box.Available && box.Selection is null && box.PackageRewards.IsDefaultOrEmpty &&
            box.Pools.Length == 1 && box.Pools[0].Draws == 1 &&
            box.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(490013318, 1000, 1) }),
            "现商店边框礼盒唯一真实奖仍为318，不替换或删除资源商品");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-strongway-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("strongway-appearance", () => { });
            var role = repository.Create(owner, new("StrongArt", towns.Room, towns.InitialReturn) { Level = 95 });
            var sibling = repository.Create(owner, new("StrongArtTwo", towns.Room, towns.InitialReturn) { Level = 95 });
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            role = InventoryService.Commit(repository, owner, role,
                inventory.Grant(role, [new(tokens[0], 1), new(tokens[1], 1), new(tokens[2], 1), new(490013316, 1)]));
            var session = new GameSession(repository, towns, owner);
            session.Select(role.Slot, resumeTown: true); session.MarkReady();
            var service = new InventorySkinStorageService(resources.Items, resources.Skins, resources.GoldLimits,
                resources.EpicPieces, resources.Contracts);
            for (int index = 0; index < tokens.Length; index++)
            {
                var source = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == tokens[index]);
                That(source.Position.List == InventoryListType.Main && source.Position.Index is >= 65 and <= 120,
                    "真实waste券由普通Grant进入消耗品页");
                foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
                    That(service.Add(session.Selected! with { Job = job }, source.Position, town: true).Accepted,
                        "真实插图券对全部职业保留合法入库：" + tokens[index] + ":" + job);
                That(!service.Add(session.Selected!, source.Position, town: false).Accepted,
                    "真实village声明在副本上下文拒绝");
                if (index == 0)
                {
                    var saved = session.Selected!;
                    string persisted = JsonSerializer.Serialize(repository.List(owner));
                    Sql("CREATE TRIGGER fail_strong_art AFTER INSERT ON account_skin_storage " +
                        "BEGIN SELECT RAISE(ABORT,'strong art rollback'); END");
                    Reject<SqliteException>(() => session.UseSkinStorage(service, source.Position),
                        "库存与审计已写后，账号插图写入故障必须传播且无成功回执");
                    That(ReferenceEquals(session.Selected, saved) &&
                        JsonSerializer.Serialize(repository.List(owner)) == persisted,
                        "晚SQL故障整笔回滚源、账号所有权、角色revision与审计，在线快照不替换");
                    Sql("DROP TRIGGER fail_strong_art");
                }
                var previous = session.Selected!;
                var result = session.UseSkinStorage(service, source.Position);
                That(result.Accepted && result.Receipt is not null && ReferenceEquals(result.Receipt.Character, session.Selected) &&
                    session.Selected!.Inventory.Find(source.Position) is null &&
                    JsonSerializer.Serialize(session.Selected!.Inventory.Items) ==
                        JsonSerializer.Serialize(previous.Inventory.Items.Remove(source)) &&
                    session.Selected!.Inventory.Skins.Cutscenes.IsEmpty &&
                    session.Selected!.Inventory.AccountSkins.Cutscenes.Any(row => row.SkinId == skins[index] && row.ExpiresAt == 0),
                    "正式Session一次扣真实源，仅将精确永久插图加入账号仓，保留其余实体");
            }
            var request = new SelectSkinRequest(1, skins[0]) { AdditionalIds = [skins[1], skins[2]] };
            That(session.PlanAndCommitInventory(character => service.Select(character, request)).Accepted &&
                session.Selected!.Inventory.Skins.SelectedCutscenes.SequenceEqual(skins) &&
                session.Selected!.Inventory.AccountSkins.SelectedCutscenes.IsEmpty,
                "三插图账号所有权可作为当前角色完整选择，账号仓不保存角色选择");
            var restored = new CharacterRepository(path).LoadCharacter(owner, role.Slot);
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(session.Selected),
                "新Repository完整恢复已消费库存、三个账号永久插图及角色选择");
            var other = new GameSession(new CharacterRepository(path), towns, owner);
            other.Select(sibling.Slot, resumeTown: true); other.MarkReady();
            That(other.Selected!.Inventory.Skins.Cutscenes.IsEmpty &&
                other.Selected!.Inventory.AccountSkins.Cutscenes.Select(row => row.SkinId).SequenceEqual(skins) &&
                other.PlanAndCommitInventory(character => service.Select(character, new(1, skins[2]))).Accepted &&
                repository.LoadCharacter(owner, role.Slot).Inventory.Skins.SelectedCutscenes.SequenceEqual(skins),
                "同账号新角色可选择账号插图，另一角色原选择不被覆盖");
            That(session.PlanAndCommitInventory(character => inventory.Grant(character, [new(tokens[0], 1)])).Accepted,
                "通过正常Grant准备一张永久重复券");
            var duplicate = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == tokens[0]);
            string duplicateBefore = JsonSerializer.Serialize(repository.List(owner));
            That(!session.UseSkinStorage(service, duplicate.Position).Accepted &&
                JsonSerializer.Serialize(repository.List(owner)) == duplicateBefore,
                "已拥有永久插图再用真实券整拒，不扣重复源或写入revision");

            var boxSource = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013316);
            var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
                resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages);
            var opened = session.PlanAndCommitBooster(character => booster.Open(character,
                new(boxSource.Position.Index, 1), out _), booster.UsePlaces(490013316), 490013316);
            That(opened.Accepted && session.Selected!.Inventory.Find(boxSource.Position)?.Core.ItemId != 490013316 &&
                session.Selected!.Inventory.Items.Count(item => item.Core.ItemId == 490013318) == 1,
                "当前礼盒正常Open仍产出一张真实318，不预改其动作到期规则");
            var expiredFrame = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013318);
            string expiredBefore = JsonSerializer.Serialize(repository.List(owner));
            That(!session.UseSkinStorage(service, expiredFrame.Position).Accepted &&
                JsonSerializer.Serialize(repository.List(owner)) == expiredBefore,
                "当前墙钟下318的2020动作期限已过，整拒且保留刚开出的券：兼容限制");

            RunHistoricalFrame();
            void RunHistoricalFrame()
            {
                using var historicalOwner = new AccountOwners().Acquire("strongway-frame-history", () => { });
                var historical = repository.Create(historicalOwner, new("OldFrame", towns.Room, towns.InitialReturn) { Level = 95 });
                var historicalSibling = repository.Create(historicalOwner, new("OldFrameTwo", towns.Room, towns.InitialReturn));
                historical = InventoryService.Commit(repository, historicalOwner, historical,
                    inventory.Grant(historical, [new(490013318, 1)]));
                var clock = new HistoricalClock(new(2020, 11, 27, 6, 0, 0, TimeSpan.FromHours(8)));
                var historicalService = new InventorySkinStorageService(resources.Items, resources.Skins, resources.GoldLimits,
                    resources.EpicPieces, resources.Contracts, clock);
                var historicalSession = new GameSession(repository, towns, historicalOwner);
                historicalSession.Select(historical.Slot, resumeTown: true); historicalSession.MarkReady();
                var source = historicalSession.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013318);
                That(historicalSession.UseSkinStorage(historicalService, source.Position).Accepted &&
                    historicalSession.Selected!.Inventory.Find(source.Position) is null &&
                    historicalSession.Selected!.Inventory.Skins.PartyFrames is [{ SkinId: 420000004, ExpiresAt: var expiry }] && expiry == frameExpiry &&
                    historicalSession.Selected!.Inventory.AccountSkins.PartyFrames.IsEmpty,
                    "仅历史冻结2020有效期正控：318按character入库并保留原固定日期");
                That(historicalSession.PlanAndCommitInventory(character => historicalService.Select(character, new(0, 420000004))).Accepted &&
                    historicalService.EffectivePartyFrame(historicalSession.Selected!) == 420000004,
                    "历史有效期正控：角色可选择真实边框");
                var historicalRestored = new CharacterRepository(path).LoadCharacter(historicalOwner, historical.Slot);
                That(JsonSerializer.Serialize(historicalRestored) == JsonSerializer.Serialize(historicalSession.Selected) &&
                    repository.LoadCharacter(historicalOwner, historicalSibling.Slot).Inventory.Skins.PartyFrames.IsEmpty,
                    "历史边框由新Repository恢复，仅当前角色拥有，不错误共享给兄弟角色");
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(frameExpiry);
                That(historicalService.EffectivePartyFrame(historicalSession.Selected!) == 20000 &&
                    historicalSession.PlanAndCommitInventory(historicalService.ExpireSelections).Accepted &&
                    historicalSession.Selected!.Inventory.Skins.SelectedPartyFrame == 20000 &&
                    !historicalSession.PlanAndCommitInventory(character => historicalService.Select(character, new(0, 420000004))).Accepted,
                    "精确到期边界回默认20000且不能再选择旧边框，不续期或改真实日期");
            }
            void Sql(string text)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
            }
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - before} 首期真实插图/边框资源、Session与SQL断言（边框当前过期拒绝，历史正控分列）");
    }

    sealed class HistoricalClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
