using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarRewardTransactionTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        string path = Path.Combine(root, "war-rewards.db");
        var repository = new CharacterRepository(path, resources.Quests);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("warrewards", () => { });
        var candidate = CharacterCreation.Prepare("WarRewards", resources.Characters.Get(0),
            catalog.WaitingArea, catalog.WaitingArea, resources.Learning.Get);
        var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
            { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
        repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
        var clock = new ConsumableUseSessionTests.FakeClock();
        var parties = new PartyService(repository, clock);
        var service = new PartyDungeonService(parties, repository, hiddenDraw: maximum => maximum - 1);
        var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
            entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock,
            incounts: DungeonIncountCatalog.Load(resources.Archive));
        try
        {
            session.Select(0, resumeTown: true); session.MarkReady(); session.CurrentParty = () => parties.Current(session);
            var policy = new EvildomWarRewardPolicy(catalog, resources.ItemResources.Items, resources.ItemResources.Boosters);
            foreach (byte mode in new byte[] { 0, 1, 2 })
                foreach (int roll in new[] { 0, 4999, 5000, 9372, 9872, 9999 })
                {
                    var choice = policy.Prepare(mode, session.Selected!, maximum => Math.Min(roll, maximum - 1));
                    var products = policy.Materialize(choice, clock.GetUtcNow());
                    That(products.Length > 0 && products.All(item => item.Uid == 0), "真实奖励表的固定物品、符文、护石及卡片都能展开完整产物");
                    foreach (var item in products) { item.Core.Validate(); item.ValidateDetails(allocating: true); }
                }
            That(parties.SetInformation(session, 0, PartySettings.Default with { Kind = 5, KindArgument = 0 }).Accepted &&
                service.StartEvildomWar(session, catalog) is null, "奖励事务夹具使用真实单人魔界队伍开战");
            var war = session.ReadEvildomWar()!;
            // 前四图仅作为本事务夹具的领域前提；完整五图真实击杀由TCP覆盖，王座仍必须实际完成Run。
            foreach (var row in war.Battle.View.Dungeons.Where(row => row.Kind == 0).ToArray())
            {
                var identity = new DungeonRunIdentity(session.Selected!.Id, 0, 1, Guid.NewGuid(), 1);
                That(war.Battle.Enter((uint)row.Id, [identity], clock.GetUtcNow()) is not null &&
                    war.Battle.Complete([new(identity, (uint)row.Id, [Guid.NewGuid()])], clock.GetUtcNow()) is not null,
                    "事务夹具准备四张前置图领域状态");
            }
            uint throne = (uint)war.Battle.View.Dungeons.Single(row => row.Kind == 1).Id;
            var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
            That(service.BeginSelection(session, throne).Length == 1, "奖励夹具进入已解锁王座");
            var entered = service.Enter(session, throne, 2, dungeons, SelfTestCatalogs.Rooms(resources.Archive), null, null, null, null);
            That(entered.Any(notice => notice.Kind == PartyDungeonNoticeKind.Entry), "王座真实入场并提交费用和尝试");
            var instance = session.PartyDungeon!;
            var entry = entered[0].Scene!.Entry;
            var route = QuestStoryRouteTests.Path(entry.Maze.StaticTopology, entry.StartMap.Position, entry.BossMap.Position);
            foreach (var point in route)
            {
                instance.CompleteLoading(session);
                var scene = session.CurrentFormalDungeonScene()!;
                foreach (var monster in scene.Monsters.Monsters.Where(monster => monster.IsBlocking && monster.CanReportDeath && !monster.IsFriendly)
                    .OrderBy(monster => monster.IsBoss))
                {
                    var observation = FormalDungeonSessionTests.Observation(scene, monster);
                    instance.ObserveDeath(session, monster.ObjectKey, current => current.ObserveFormalDungeonDeath(observation,
                        SelfTestCatalogs.KillExperience(resources.Archive), resources.Growth));
                }
                if (point == route[^1]) break;
                var next = route[Array.IndexOf(route, point) + 1];
                That(instance.Move(session, (byte)next.X, (byte)next.Y, 0).Length == 1, "奖励夹具沿真实王座路线推进");
            }
            That(war.Battle.Completion is not null, "只有真实王座终点ClearFact才可规划奖励");
            int talisman = resources.ItemResources.Items.TalismanCandidates(3).First();
            // 强制十二件非堆叠护石，覆盖跨邮件批次的完整实例与事务原子性。
            war.RewardSelection = new(0, [new(2, talisman, 12)]);
            string Assets() => Scalar("SELECT count(*) FROM mailbox_messages") + ":" + Scalar("SELECT count(*) FROM character_inventory_items") +
                ":" + Scalar("SELECT count(*) FROM mailbox_attachments");
            string initial = Assets();
            Sql("CREATE TRIGGER reject_war_reward BEFORE UPDATE ON character_evildom_war_rewards BEGIN SELECT RAISE(ABORT,'war reward failure'); END");
            Reject<SqliteException>(() => session.CommitEvildomWarReward(policy, repository), "邮件插入后回执SQL失败");
            var frozen = war.RewardPlan!;
            That(Assets() == initial && war.RewardCommit is null && frozen.Items.Length == 12 &&
                Scalar("SELECT count(*) FROM character_evildom_war_rewards WHERE delivered=0") is 1L,
                "失败回滚两封邮件和十二件附件，原完整抽样已持久冻结供重试");
            Sql("DROP TRIGGER reject_war_reward");
            var reopened = new CharacterRepository(path, resources.Quests);
            That(reopened.RecoverEvildomWarRewards(owner, session.Selected!.Id).Length == 2 &&
                reopened.RecoverEvildomWarRewards(owner, session.Selected.Id).IsEmpty,
                "重建仓储后无需旧Run，恢复原冻结完整资产且重复恢复不再交付");
            var receipt = session.CommitEvildomWarReward(policy, repository)!;
            That(ReferenceEquals(receipt.Commit.Plan, frozen) && receipt.Commit.MailIds.Length == 2 &&
                Scalar("SELECT count(*) FROM mailbox_attachments") is 12L &&
                Scalar("SELECT count(*) FROM mailbox_messages WHERE expires_at IS NULL") is 2L,
                "原计划恢复后全部附件分两封永久邮件同事务保存");
            string committed = Assets();
            var duplicate = reopened.CommitEvildomWarReward(owner, frozen);
            That(duplicate.Duplicate && duplicate.MailIds.SequenceEqual(receipt.Commit.MailIds) && Assets() == committed,
                "重建仓储后仍返回原邮件，不重抽或重发");
            var changed = frozen.Reward with { Items = [new(2, talisman, 13)] };
            Reject<InvalidDataException>(() => reopened.CommitEvildomWarReward(owner,
                frozen with { Reward = changed, Items = policy.Materialize(changed, frozen.Completion.Created) }), "同战役更改冻结奖励拒绝");
            Sql("UPDATE mailbox_messages SET state=3");
            That(reopened.CommitEvildomWarReward(owner, frozen).Duplicate && Assets() == committed,
                "删除邮件状态不能绕过独立回执再发奖");
            session.ConfirmEvildomWarRewardPublished(receipt);
            That(!session.ShouldPublishEvildomWarReward(receipt), "结算通知成功发布后不重复弹窗");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => reopened.CommitEvildomWarReward(owner, frozen), "旧generation不能发奖");
            That(!session.CanPublishEvildomWarReward(receipt), "接管后旧奖励通知失效");
            session.End();
            reopened.Delete(replacement, reopened.LoadDeletionTarget(replacement, 0));
            That(Scalar("SELECT count(*) FROM character_evildom_war_rewards") is 0L, "真实删角清理独立奖励回执及邮件资产");
        }
        finally { session.End(); }
        Console.WriteLine($"PASS evildom war reward transactions {Count - before}");

        void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        object? Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
    }
}
