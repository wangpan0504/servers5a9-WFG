using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongMailResourceTests
{
    internal static void Run(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-song-mail-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "song-mail-resource", season = "song-mail-resource-season";
            const int itemId = BattlePassUndefeatedSongSource.ItemId;
            var items = resources.ItemResources;
            var template = items.Items.Get(itemId);
            That(template is { Kind: ItemKind.Material, AccountMaterial.HoldingLimit: >= 40 },
                "匹配资源的10308807为足容40件的账号材料");
            var catalog = BattlePassCatalog.Load(resources.Archive);
            var now = DateTimeOffset.UtcNow;
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var thursday = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, TimeSpan.FromHours(8))
                .AddDays(-(((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
            if (thursday > local) thursday = thursday.AddDays(-7);
            BattlePassMissionPolicy? policy = null;
            for (int week = 0; week < catalog.WeeklyMissionIds.Length / 4; week++)
            {
                var candidate = new BattlePassMissionPolicy(season, thursday.AddDays(-7 * week), catalog);
                if (candidate.Active(now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition)))
                { policy = candidate; break; }
            }
            if (policy is null) throw new InvalidDataException("匹配资源未提供活跃周任务48");
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            repository.Create(owner, new NewCharacter("SongMail", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner);
            session.Select(0); session.MarkReady();
            var service = new MailboxService(repository, items.Items, items.GoldLimits, items.EpicPieces);
            var recipient = service.FindRecipient("SongMail") ?? throw new InvalidDataException("测试角色不存在");
            uint Deliver(int count, string key)
            {
                var delivery = service.Deliver(new MailboxDelivery(key, recipient, "系统", "材料附件", 0,
                    [new(0, new(InventoryListType.Main, 0), InventoryRewardGrantService.Create(template, count))], now.ToUnixTimeSeconds()));
                That(delivery.Accepted, "原邮件真实投递成功");
                return service.Open(owner, recipient.Id, now).Entries.Single(row => row.Id == delivery.MailId).Attachments.Single().ClaimId;
            }
            uint first = Deliver(25, "song-mail-first");
            uint second = Deliver(15, "song-mail-second");
            var firstResult = session.ClaimMail(service, [first]);
            That(firstResult.Accepted && session.Selected!.Inventory.AccountWallet.Material(itemId).Balance == 25 &&
                session.Selected.Inventory.Items.All(item => item.Core.ItemId != itemId) && MissionCount() == 25,
                "真实附件25转入账号余额并推进原任务25");
            var duplicate = session.ClaimMail(service, [first]);
            That(!duplicate.Accepted && session.Selected!.Inventory.AccountWallet.Material(itemId).Balance == 25 && MissionCount() == 25,
                "重复领取不重复增加余额或进度");
            var secondResult = session.ClaimMail(service, [second]);
            var persisted = repository.LoadCharacter(owner, 0)!;
            That(secondResult.Accepted && session.Selected!.Inventory.AccountWallet.Material(itemId).Balance == 40 &&
                persisted.Inventory.AccountWallet.Material(itemId).Balance == 40 &&
                persisted.Inventory.Items.All(item => item.Core.ItemId != itemId) && MissionCount() == 40,
                "第二附件增加15，重读余额及原任务累计40");

            long MissionCount() => repository.ReadBattlePassMissions(owner, DateTimeOffset.UtcNow)!.Slots
                .Single(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition)).Count;
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
