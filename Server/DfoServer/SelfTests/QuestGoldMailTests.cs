using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestGoldMailTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, SkillLearningCatalog learning,
        CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters)
    {
        const uint amount = 756;
        uint cap = checked((uint)items.GoldLimits.CarryLimit(1));
        var quest = QuestEligibilityTests.Quest(100) with
        {
            IntData = [2], Npc = 2, CompleteNpc = 2, Attributes = ["not give exp quest"],
            Rewards = [new(0, 0, null), new(0, (int)amount, null), new(0, 2058, null), new(0, 1, null)]
        };
        foreach (uint balance in new[] { cap - amount, cap - amount + 1, cap, 100000001u })
        {
            using var f = Make(quest, balance);
            var before = f.Read();
            var plan = f.Prepare(ushort.MaxValue).Plan ?? throw new InvalidOperationException("金币任务规划未接受");
            bool mailed = balance + amount > cap;
            That(plan.GoldAmount == amount && plan.MailedGoldAmount == (mailed ? amount : 0) &&
                plan.DirectGoldAmount == (mailed ? 0 : amount) &&
                plan.Inventory.After.Gold == balance + plan.DirectGoldAmount &&
                plan.Snapshot!.Contains("MailedGoldAmount", StringComparison.Ordinal) == mailed,
                "任务金币按完整容量选择整笔直接到账或整笔永久邮件，普通Snapshot不增字段");
            var commit = f.Repository.CommitQuestCompletion(f.Owner, plan);
            That(commit.GoldAmount == commit.DirectGoldAmount + commit.MailedGoldAmount &&
                commit.Character.Inventory.Gold == before.Inventory.Gold + commit.DirectGoldAmount &&
                commit.Character.CompletedQuests.Contains(100) && commit.Character.ActiveMainQuests.Length == 0 &&
                commit.Rewards.Single().GrantedCount == 1 && f.Receipts == 1,
                "金币守恒且不阻塞物品奖励、完成位、清槽和唯一回执");
            var mailbox = f.Repository.LoadMailbox(f.Owner, before.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(mailed ? mailbox.Entries is [var mail] && mail.Id == commit.GoldMailId && mail.Gold == amount &&
                mail.System && mail.Expires is null && mail.Attachments.IsEmpty : mailbox.Entries.IsEmpty && commit.GoldMailId is null,
                "实际邮件ID绑定原接取，纯金币永久邮件不造附件UID，容量足够时不发邮件");
            string saved = Snapshot(f);
            var replay = f.Repository.CommitQuestCompletion(f.Owner, plan);
            That(replay.Duplicate && replay.GoldMailId == commit.GoldMailId && replay.DirectGoldAmount == commit.DirectGoldAmount &&
                Snapshot(f) == saved, "原冻结回执重放只恢复结果，不再发金币、邮件或物品");
        }

        var paidQuest = quest with { Condition = "[seeking]", IntData = [0, 1000], Rewards = [new(0, 0, null), new(0, (int)amount, null)] };
        using (var f = Make(paidQuest, cap + 200))
        {
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(plan.Consumed.Single().Count == 1000 && plan.MailedGoldAmount == 0 &&
                plan.Inventory.After.Gold == cap - 800 + amount,
                "先扣任务金币材料再核对奖励容量，扣款腾出的空间可以直接接纳完整奖励");
        }
        foreach (string failure in new[] { "receipt", "mail-audit" })
        {
            using var f = Make(quest, cap);
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            string table = failure == "receipt" ? "character_quest_completion_receipts" : "mailbox_audit_log";
            f.Sql("CREATE TRIGGER gold_mail_abort AFTER INSERT ON " + table + " BEGIN SELECT RAISE(ABORT,'gold mail rollback'); END");
            string before = Snapshot(f);
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner, plan),
                "任务回执或邮件末段实际SQL失败");
            That(Snapshot(f) == before && f.Receipts == 0, "失败回滚奖励UID、Gold、清槽、完成位、邮件及序列全部状态");
            f.Sql("DROP TRIGGER gold_mail_abort");
            That(f.Repository.CommitQuestCompletion(f.Owner, plan).GoldMailId is not null && f.Receipts == 1,
                "回滚后原冻结计划可以提交，不重抽或改奖励资格");
        }
        using (var f = Make(quest, cap, lost: true))
        {
            Reject<IOException>(() => f.Finish(ushort.MaxValue), "金币邮件和完成事务已提交但返回结果丢失");
            var plan = f.Lost!.FirstPlan!;
            var recovered = f.Finish(ushort.MaxValue).Receipt!.Commit;
            That(recovered.Duplicate && recovered.GoldMailId is not null && f.Lost!.SamePlan && f.Receipts == 1 &&
                f.Repository.LoadMailbox(f.Owner, recovered.Character.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries.Length == 1,
                "Session原计划恢复唯一邮件，重复请求不改变冻结资格");
            var service = new MailboxService(f.Repository, items.Items, items.GoldLimits, items.EpicPieces);
            That(f.Session.ClaimMail(service, [recovered.GoldMailId!.Value]).Error == MailboxError.GoldCarryLimit,
                "金币仍无空间时领取拒绝，永久邮件保留完整额度");
            f.Session.SetCharacterGold(f.Read().Name, 0);
            var claim = f.Session.ClaimMail(service, [recovered.GoldMailId.Value]);
            That(claim.Accepted && f.Read().Inventory.Gold == amount &&
                f.Repository.LoadMailbox(f.Owner, recovered.Character.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries.Single().Gold == 0,
                "腾出容量后经原邮箱领取一次，金币与邮件状态共同提交");
            f.Session.ChangeMailState(service, [recovered.GoldMailId.Value], 0);
            string saved = Snapshot(f);
            var replay = f.Repository.CommitQuestCompletion(f.Owner, plan);
            That(replay.Duplicate && replay.GoldMailId == recovered.GoldMailId && !replay.ProjectionCurrent && Snapshot(f) == saved,
                "邮件已领取及删除后按原delivery_content恢复ID，不以mutableGold0重建或补发");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().Inventory.Gold == amount && f.Read().CompletedQuests.Contains(100), "重选保留实际金币与任务完成");
            f.Session.ReturnToRoster(); f.Session.DeleteCharacter(0);
            using var db = f.Open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT (SELECT count(*) FROM mailbox_messages)+(SELECT count(*) FROM mailbox_audit_log)+(SELECT count(*) FROM character_quest_completion_receipts)";
            That(query.ExecuteScalar() is 0L, "删除收件角色沿已有清理移除私有任务金币邮件、审计和完成回执");
        }
        foreach (bool missing in new[] { false, true })
        {
            using var f = Make(quest, cap);
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            f.Repository.CommitQuestCompletion(f.Owner, plan);
            f.Sql(missing ? "DELETE FROM mailbox_audit_log; DELETE FROM mailbox_messages" : "UPDATE mailbox_messages SET delivery_content='{}'");
            string before = Snapshot(f);
            Reject<InvalidDataException>(() => f.Repository.CommitQuestCompletion(f.Owner, plan), "原金币邮件缺失或冻结内容变化时拒绝回执重放");
            That(Snapshot(f) == before, "异常邮件不重建、不补发，不改写原完成回执");
        }
        using (var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters, items,
            quest with { Rewards = [new(0, 0, null), new(0, (int)amount, null), new(0, 10403, null), new(0, 1, null)] },
            materialCount: 0, fullEquipment: true, active: [new(0, 100, 0)],
            configure: candidate => candidate with { Inventory = candidate.Inventory with { Gold = cap } }))
        {
            var result = f.Finish(ushort.MaxValue);
            That(result.Outcome == QuestFinishOutcome.InsufficientSpace && result.Failure == QuestCompletionFailure.InventoryCapacity &&
                f.Receipts == 0 && f.Repository.LoadMailbox(f.Owner, f.Read().Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries.IsEmpty,
                "物品仍满包时整笔任务按原容量拒绝，不因金币可邮寄而部分完成");
        }

        var resourceQuest = quest with { Definition = quest.Definition! with { Fields = [
            new("mail time",[new(0,1,null)],[]), new("mail title",[new(6,0,"任务声明标题")],[]),
            new("mail contents",[new(6,0,"系统奖励正文")],[]), new("mail enclose",[new(0,2058,null),new(0,2,null)],[])] } };
        using (var f = Make(resourceQuest,0))
        {
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(plan.ResourceMail is { Attachments.Length:1 } && plan.ResourceMail.Attachments[0].Core.Count == 2,
                "声明附件独立冻结，不能复制普通任务奖励或乘领奖倍数");
            f.Sql("CREATE TRIGGER quest_resource_mail_abort AFTER INSERT ON mailbox_audit_log BEGIN SELECT RAISE(ABORT,'resource mail rollback'); END");
            string before = Snapshot(f);
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"邮件末段失败回滚整笔任务");
            That(Snapshot(f)==before,"邮件、附件UID、经验金币、任务完成与回执均未部分提交");
            f.Sql("DROP TRIGGER quest_resource_mail_abort");
            var committed=f.Repository.CommitQuestCompletion(f.Owner,plan);
            var mailbox=f.Repository.LoadMailbox(f.Owner,committed.Character.Id,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(mailbox.Entries is [var mail] && mail.Id==committed.ResourceMailId && mail.System && mail.SenderId==0 &&
                mail.SenderName=="系统任务" && mail.Text=="任务声明标题\n系统奖励正文" && mail.Expires is null && mail.Gold==0 &&
                mail.Attachments is [var attachment] && attachment.Item.Core.ItemId==2058 && attachment.Item.Core.Count==2 &&
                committed.Rewards.Single().GrantedCount==1,
                "标题保留正文且来源明确为系统，声明附件与普通奖励各发原数量一次");
            string saved=Snapshot(f);
            var replay=f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(replay.Duplicate && replay.ResourceMailId==committed.ResourceMailId && Snapshot(f)==saved,
                "冻结任务重放恢复原邮件，不重建附件或再发奖励");
        }

        QuestOrdinaryCompletionTests.Fixture Make(StoryQuest definition, uint balance, bool lost = false) =>
            new(towns, characters, learning, growth, parameters, items, definition, materialCount: 0, loseResult: lost,
                active: [new(0, 100, 0)], configure: candidate => candidate with { Inventory = candidate.Inventory with { Gold = balance } });
    }

    static string Snapshot(QuestOrdinaryCompletionTests.Fixture fixture)
    {
        using var db = fixture.Open(); using var query = db.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        var content = new List<object[]>();
        foreach (string table in tables)
        {
            query.CommandText = "SELECT rowid,* FROM \"" + table + "\" ORDER BY rowid";
            using var reader = query.ExecuteReader();
            while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); content.Add(values); }
        }
        return JsonSerializer.Serialize(content);
    }
}
