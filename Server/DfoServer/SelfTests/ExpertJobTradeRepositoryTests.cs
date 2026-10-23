using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobTradeRepositoryTests
{
    public static void Run()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-trade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var customerLease = owners.Acquire("trade-customer", () => { });
            using var ownerLease = owners.Acquire("trade-owner", () => { });
            var customer = repository.Create(customerLease, new("Customer", new(38, 1, 450, 234, 0), null)
            {
                Level = 50,
                Inventory = new([new(0, new(InventoryListType.Main, 121), new ItemCore(10014751, ItemKind.Material, 10))], Gold: 1000)
            });
            var owner = repository.Create(ownerLease, new("Owner", new(38, 1, 450, 234, 0), null)
            { Level = 50, Inventory = new([], Gold: 100) });
            Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) VALUES({owner.Id},'trade-owner',1,51,'[10015129]')");
            owner = repository.LoadCharacter(ownerLease, owner.Slot);
            var paid = Plan(10);
            Sql("CREATE TRIGGER fail_trade BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'trade final failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, paid), "双角色写入后耐久失败整笔回滚");
            Unchanged("末段失败保持双方资产、版本、资格和原UID");
            Sql("DROP TRIGGER fail_trade");

            foreach (bool customerSide in new[] { true, false })
            {
                var stale = customerSide ? paid with { CustomerExpected = customer with { Revision = customer.Revision + 1 } }
                    : paid with { OwnerExpected = owner with { Revision = owner.Revision + 1 } };
                Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, stale), "任一方旧版本拒绝");
                Unchanged("任一方版本冲突不部分提交");
            }
            foreach (var (change, restore) in new[]
            {
                ($"UPDATE characters SET gold=999 WHERE id={customer.Id}", $"UPDATE characters SET gold=1000 WHERE id={customer.Id}"),
                ($"UPDATE characters SET gold=99 WHERE id={owner.Id}", $"UPDATE characters SET gold=100 WHERE id={owner.Id}"),
                ($"UPDATE characters SET level=51 WHERE id={customer.Id}", $"UPDATE characters SET level=50 WHERE id={customer.Id}"),
                ($"UPDATE characters SET level=51 WHERE id={owner.Id}", $"UPDATE characters SET level=50 WHERE id={owner.Id}"),
                ($"UPDATE character_expert_job SET job_type=3 WHERE character_id={owner.Id}", $"UPDATE character_expert_job SET job_type=1 WHERE character_id={owner.Id}"),
                ($"UPDATE character_expert_job SET expertness=52 WHERE character_id={owner.Id}", $"UPDATE character_expert_job SET expertness=51 WHERE character_id={owner.Id}"),
                ($"UPDATE character_expert_job SET learned_recipes='[]' WHERE character_id={owner.Id}", $"UPDATE character_expert_job SET learned_recipes='[10015129]' WHERE character_id={owner.Id}"),
                ($"INSERT INTO character_expert_meta(character_id,account,giveup_state,machine_grade,machine_endurance) VALUES({owner.Id},'trade-owner',0,2,300)", $"DELETE FROM character_expert_meta WHERE character_id={owner.Id}"),
                ($"INSERT INTO character_expert_meta(character_id,account,giveup_state,machine_grade,machine_endurance) VALUES({owner.Id},'trade-owner',0,1,299)", $"DELETE FROM character_expert_meta WHERE character_id={owner.Id}")
            })
            {
                Sql(change);
                Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, paid), "不推进版本的资格修改拒绝旧计划");
                Sql(restore);
                Unchanged("资格冲突不消费双方资产");
            }
            Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, customerLease, paid), "同账号交易拒绝");
            Reject<InvalidDataException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, paid with { Fee = 11 }), "转账金额必须守恒");
            var injected = new InventoryPlan(owner.Inventory, paid.OwnerInventory.After with { WinPoints = 1 }, "expert_trade");
            Reject<InvalidDataException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, paid with { OwnerInventory = injected }), "摊主只能收到金币不能夹带资产");
            Unchanged("非法交易不产生写入");

            Commit(paid);
            Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, paid), "付费交易不能重放");
            var free = Plan(0);
            That(!free.OwnerInventory.Changed, "免费服务没有伪造摊主资产变化");
            Sql("CREATE TRIGGER fail_free_trade BEFORE INSERT ON character_expert_meta BEGIN SELECT RAISE(ABORT,'free final failure'); END");
            Reject<SqliteException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, free), "免费服务的单独摊主版本写入也随失败回滚");
            Unchanged("免费服务末段失败不留下任何角色版本或资产变化");
            Sql("DROP TRIGGER fail_free_trade");
            Commit(free);
            Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, free), "免费交易也不能重放");
            Commit(Plan(7, systemGoldCost: 3, customerGoldReward: 2));
            foreach (bool busyAfterCommit in new[] { false, true })
            {
                int attempts = 0, ownerPublications = 0;
                long beforeGold = customer.Inventory.Gold, beforeRevision = customer.Revision;
                var dispatched = SqliteBusyRetry.TryExecuteAsync(async () =>
                {
                    attempts++;
                    Commit(Plan(2));
                    var failure = await ExpertJobTradePublication.PublishAsync(
                        () => busyAfterCommit ? Task.CompletedTask : throw new IOException("customer closed after commit"),
                        () => { ownerPublications++; return busyAfterCommit ? throw new SqliteException("read busy after commit", 5) : Task.CompletedTask; });
                    return failure is not null;
                }, CancellationToken.None).GetAwaiter().GetResult();
                That(dispatched.Completed && dispatched.Value && attempts == 1 && ownerPublications == 1 &&
                    customer.Inventory.Gold == beforeGold - 2 && customer.Revision == beforeRevision + 1,
                    "真实SQLite提交后投递断线或BUSY不触发外层重规划、不二次扣费，另一端仍尝试投影");
                Unchanged("发布故障只影响连接，不回滚或重复已提交资产");
            }
            using (var replacement = owners.Acquire(ownerLease.Account, () => { }))
            {
                Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, ownerLease, Plan(1)), "旧摊主租约不能成交");
                That(repository.LoadCharacter(replacement, owner.Slot).Revision == owner.Revision, "摊主接管不消费旧计划");
            }
            using (var replacement = owners.Acquire(customerLease.Account, () => { }))
            using (var freshOwner = owners.Acquire(ownerLease.Account, () => { }))
            {
                Reject<InvalidOperationException>(() => repository.CommitExpertJobTrade(customerLease, freshOwner, Plan(1)), "旧顾客租约不能成交");
                That(repository.LoadCharacter(replacement, customer.Slot).Revision == customer.Revision, "顾客接管不消费旧计划");
            }

            ExpertJobTradePlan Plan(uint fee, uint systemGoldCost = 0, uint customerGoldReward = 0)
            {
                var item = customer.Inventory.Items.Single();
                var after = customer.Inventory with { Gold = checked(customer.Inventory.Gold + customerGoldReward - systemGoldCost - fee),
                    Items = customer.Inventory.Items.Replace(item, item with { Core = item.Core with { Value = item.Core.Value - 1 } }) };
                return new(customer, owner, new(customer.Inventory, after, "expert_trade_customer"),
                    new(owner.Inventory, owner.Inventory with { Gold = owner.Inventory.Gold + fee }, "expert_trade_owner"),
                    owner.ExpertJobExperience + 1, owner.ExpertMachineEndurance - 1, fee, systemGoldCost, customerGoldReward);
            }
            void Commit(ExpertJobTradePlan plan)
            {
                var committed = AccountOwners.Lease.ExecuteTogether(customerLease, ownerLease,
                    () => repository.CommitExpertJobTrade(customerLease, ownerLease, plan));
                That(committed.Customer.Revision == customer.Revision + 1 && committed.Owner.Revision == owner.Revision + 1,
                    "付费或免费服务双方各推进且仅推进一次版本");
                That((long)committed.Customer.Inventory.Gold + committed.Owner.Inventory.Gold ==
                    (long)customer.Inventory.Gold + owner.Inventory.Gold + plan.CustomerGoldReward - plan.SystemGoldCost &&
                    committed.Customer.Inventory.Items.Single().Uid == customer.Inventory.Items.Single().Uid &&
                    committed.Customer.Inventory.Items.Single().Core.Value == customer.Inventory.Items.Single().Core.Value - 1 &&
                    committed.Owner.ExpertJobExperience == plan.OwnerExperience && committed.Owner.ExpertMachineEndurance == plan.OwnerEndurance,
                    "顾客消耗与原UID、费用守恒、摊主熟练度耐久一并提交");
                customer = committed.Customer;
                owner = committed.Owner;
                Unchanged("重载双方得到本次实际提交快照");
            }
            void Unchanged(string message)
            {
                var actualCustomer = repository.LoadCharacter(customerLease, customer.Slot);
                var actualOwner = repository.LoadCharacter(ownerLease, owner.Slot);
                That(actualCustomer.Revision == customer.Revision && actualOwner.Revision == owner.Revision &&
                    actualCustomer.Inventory.Gold == customer.Inventory.Gold && actualOwner.Inventory.Gold == owner.Inventory.Gold &&
                    actualCustomer.Inventory.Items.SequenceEqual(customer.Inventory.Items) &&
                    actualOwner.ExpertJobExperience == owner.ExpertJobExperience && actualOwner.ExpertMachineEndurance == owner.ExpertMachineEndurance &&
                    actualOwner.ExpertJobRecipes.SetEquals(owner.ExpertJobRecipes), message);
            }
            void Sql(string sql) { using var db = database.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 副职业双角色事务、费用守恒、资格复验和回滚断言");
    }
}
