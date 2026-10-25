using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ImmutableArray<uint> CommitQuestEligibilityMail(AccountOwners.Lease owner, QuestEligibilityMailPlan plan) => owner.Execute(() =>
    {
        if (plan.Mails.IsEmpty) return ImmutableArray<uint>.Empty;
        if (quests is null) throw new InvalidOperationException("任务资格邮件缺少权威任务目录");
        using var db=database.Open();
        using var write=db.BeginWrite("CharacterRepository.CommitQuestEligibilityMail",deferred:true);
        using var tx=write.Transaction;
        var expected=plan.Expected;
        var current=Require(db,tx,owner.Account,expected.Name);
        if (current.Id!=expected.Id || current.Revision!=expected.Revision)
            throw new InvalidOperationException("首次资格邮件的角色快照已失效");
        var delivered=ImmutableArray.CreateBuilder<uint>();
        foreach(var mail in plan.Mails)
        {
            var quest=quests.Get(mail.QuestId);
            if (!QuestEligibilityMailPlan.Declaration(quest) || mail.ResourcePath!=quest.Path ||
                mail.Declaration.Time!=0 || mail.Declaration.Body!=QuestMailPlan.Read(quest).Body || !mail.Declaration.Enclosures.IsEmpty || !mail.Attachments.IsEmpty)
                throw new InvalidDataException("首次资格邮件与原资源合同不符");
            // 必须重新验证完整资格，不能以客户端展示或已计划邮件代替任务前置。
            if (current.ActiveMainQuests.Length>=30 || !QuestEligibility.Evaluate(current,quest,quests).Eligible)
                throw new InvalidOperationException("首次资格邮件的接取条件已变化");
            int scope=quest.Definition!.AccountQuest ? 0 : current.Id;
            using var command=db.CreateCommand(); command.Transaction=tx;
            command.Parameters.AddWithValue("$account",owner.Account);
            command.Parameters.AddWithValue("$scope",scope);
            command.Parameters.AddWithValue("$quest",quest.Id);
            command.CommandText="SELECT mail_id FROM quest_eligibility_mail_dispatches WHERE account=$account AND scope_character_id=$scope AND quest_id=$quest";
            if (command.ExecuteScalar() is not null) continue;
            var delivery=new MailboxDelivery("quest-invitation:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(owner.Account)))+":"+scope+":"+quest.Id,
                new(current.Id,owner.Account,current.Name,current.Job,current.GrowType,current.Level),
                QuestMailPlan.Sender,mail.Declaration.Body,0,[],mail.FrozenAt.ToUnixTimeSeconds());
            var (error,id)=DeliverMailInTransaction(db,tx,delivery);
            if (error!=MailboxError.None) throw new InvalidOperationException("首次资格邮件投递失败："+error);
            command.Parameters.AddWithValue("$recipient",current.Id);command.Parameters.AddWithValue("$mail",id);
            command.Parameters.AddWithValue("$resource",mail.ResourcePath);command.Parameters.AddWithValue("$at",mail.FrozenAt.ToUnixTimeSeconds());
            command.CommandText="INSERT INTO quest_eligibility_mail_dispatches(account,scope_character_id,quest_id,recipient_id,mail_id,resource_path,dispatched_at) VALUES($account,$scope,$quest,$recipient,$mail,$resource,$at)";
            command.ExecuteNonQuery();delivered.Add(id);
        }
        tx.Commit();return delivered.ToImmutable();
    });
}
