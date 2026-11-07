using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunSortedStackCards(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-sorted-card-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("sorted-card", () => { });
            var growth = SelfTestCatalogs.Growth(pvf);
            var template = items.Items.Get(10305892);
            var policy = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                items.Items, items.GoldLimits, items.EpicPieces);
            var now = DateTimeOffset.UtcNow;
            var reward = new DungeonCardReward(8125, InventoryRewardGrantService.Create(template, 75),
                "fixture-sorted-card", "etc/itemdropinfo_clearreward.etc", now.ToUnixTimeSeconds());
            var bonus = reward with { Gold = 0, Item = reward.Item! with { Value = 25 } };
            var options = new DungeonCardOptions(bonus, 100, false, new(), bonus);
            foreach (byte sortLock in new byte[] { 0, 1 })
            {
                var candidate = Fixture("SortedCard" + sortLock);
                var position = new InventorySlot(InventoryListType.Main, ItemSlotBoundService.Range(template.Kind, candidate.Inventory).Start);
                var core = reward.Item! with { Value = 29, SortLockFlag = sortLock };
                var initial = repository.Create(owner, candidate with { Inventory = candidate.Inventory with
                    { Gold = 10000, Items = candidate.Inventory.Items.Add(new(0, position, core)) } });
                long uid = initial.Inventory.Find(position)!.Uid;
                var (run, authority) = CreateRun(initial);
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
                var prepared = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward, reward, options).Pending!;
                var settled = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth).Character;
                var delivery = policy.PlanDelivery(settled, [reward, bonus], settled.Level);
                foreach (var forged in new[]
                {
                    delivery.Inventory.After.Find(position)!.Core with { Value = 130 },
                    delivery.Inventory.After.Find(position)!.Core with { SortLockFlag = (byte)(1 - sortLock) },
                    delivery.Inventory.After.Find(position)!.Core with { Attr = 1 }
                })
                {
                    var after = delivery.Inventory.After with { Items = [.. delivery.Inventory.After.Items.Select(item =>
                        item.Position == position ? item with { Core = forged } : item)] };
                    var invalid = delivery with { Inventory = new(delivery.Inventory.Before, after, "dungeon_card_reward") };
                    Reject<InvalidDataException>(() => repository.CommitFreeDungeonCard(owner, settled, prepared, invalid),
                        "真正超额、改变目标排序锁或完整属性仍拒绝且不消费资格");
                    That(SkillRepositoryTests.Equal(settled, repository.LoadCharacter(owner, initial.Slot)),
                        "无效翻牌计划拒绝后角色及资产保持原样");
                }
                Sql("CREATE TRIGGER sorted_card_fail BEFORE UPDATE OF claimed ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'sorted card rollback'); END");
                Reject<SqliteException>(() => repository.CommitFreeDungeonCard(owner, settled, prepared, delivery),
                    "排序锁合堆领取位写入失败回滚金币、叠加数量与会员奖励");
                That(SkillRepositoryTests.Equal(settled, repository.LoadCharacter(owner, initial.Slot)), "领取末段失败不留下部分资产");
                Sql("DROP TRIGGER sorted_card_fail");
                var claimed = repository.CommitFreeDungeonCard(owner, settled, prepared, delivery);
                var actual = claimed.Character.Inventory.Find(position)!;
                That(!claimed.Duplicate && claimed.MailId is null && actual.Uid == uid &&
                    actual.Core == core with { Value = 129 } && claimed.Character.Inventory.Gold == 18125,
                    "普通和排序锁堆叠均接收免费牌及黑钻奖励，保持原UID和目标锁");
                That(repository.CommitFreeDungeonCard(owner, settled, prepared, delivery) is { Duplicate: true } duplicate &&
                    SkillRepositoryTests.Equal(claimed.Character, duplicate.Character), "免费牌重放不重复叠加");
                var paidDelivery = policy.PlanDelivery(claimed.Character, [bonus], claimed.Character.Level, options.Price, now);
                var paid = repository.CommitPaidDungeonCard(owner, claimed.Character, prepared, paidDelivery, now);
                That(paid is { Accepted: true, Duplicate: false, Character: not null } &&
                    paid.Character.Inventory.Find(position) is { } paidStack && paidStack.Uid == uid &&
                    paidStack.Core == core with { Value = 154 } && paid.Character.Inventory.Gold == 18025,
                    "付费牌共用校验也能叠入排序锁堆叠并准确扣费");
                That(SkillRepositoryTests.Equal(paid.Character!, new CharacterRepository(path).LoadCharacter(owner, initial.Slot)),
                    "重开仓储保留真实数量、排序锁与金币");
            }

            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 翻牌排序锁合堆、严格拒绝、原子回滚与恢复断言");
    }
}
