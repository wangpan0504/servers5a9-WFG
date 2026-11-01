using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class ItemUsePersistenceTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-item-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("item-use", () => { });
            var now = DateTimeOffset.UtcNow;
            var template = resources.Items.Get(10400);
            var equipment = InventoryRewardGrantService.Create(template, utcNow: now);
            repository.Create(owner, new("Overflow", towns.Room, towns.InitialReturn)
            {
                Inventory = new([
                    .. Enumerable.Range(9, 56).Select(slot => new InventoryItem(0, new(InventoryListType.Main, (short)slot), equipment)),
                    new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.Items.Get(7542), 1, now))])
            });
            var current = repository.List(owner).Single();
            var paid = InventoryDeleteService.Plan(current.Inventory, new(InventoryListType.Main, 65), 1, 1).Plan!.After;
            var grant = InventoryRewardGrantService.TryGrantBatch(current with { Inventory = paid }, [new(10400, 1, 3, 2)],
                resources.Items, resources.GoldLimits, resources.EpicPieces, now, allowOverflowMail: true);
            That(grant.Accepted, "满装备页仍能规划礼包奖励与溢出投递");
            var committed = repository.CommitInventory(owner, current,
                new(current.Inventory, grant.Plan!.After, "booster_open", overflowItems: grant.Plan.OverflowItems));
            var page = repository.LoadMailbox(owner, current.Id, now.ToUnixTimeSeconds());
            That(page.Entries.Length == 1 && page.Entries[0].System && page.Entries[0].Attachments.Length == 1,
                "礼包溢出进入现有真实收件箱，不是不可领取的平行暂存表");
            var attachment = page.Entries[0].Attachments[0];
            That(attachment.Item.Uid > 0 && attachment.Item.Core.Upgrade == 3 && attachment.Item.Core.GenuineUpgrade == 2,
                "邮件保留完整奖励核心与分配后的真实UID");
            var freed = InventoryDeleteService.Plan(committed.Inventory, new(InventoryListType.Main, 9), equipment.Value, 1);
            current = repository.CommitInventory(owner, committed, freed.Plan!);
            var mailbox = new MailboxService(repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
            That(mailbox.PlanClaim(owner, current, [attachment.ClaimId], now, out var claim) == MailboxError.None,
                "释放对应类型栏位后能够领取溢出附件");
            var claimed = mailbox.Claim(owner, current, claim!);
            That(claimed.Accepted && claimed.Character!.Inventory.Find(new(InventoryListType.Main, 9)) is { } reward &&
                reward.Uid == attachment.Item.Uid && reward.Core == attachment.Item.Core,
                "领取实时回填装备栏并保留原实例，不重新随机或重建属性");
            var recovered = new CharacterRepository(path).List(owner).Single();
            That(recovered.Inventory.Find(new(InventoryListType.Main, 9))?.Uid == attachment.Item.Uid,
                "重开数据库后溢出奖励仍属于相同角色与栏位");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - before} 礼包满包真实邮件、完整核心及领取恢复断言");
    }
}
