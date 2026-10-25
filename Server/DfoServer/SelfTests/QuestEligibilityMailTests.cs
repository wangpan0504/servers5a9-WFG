using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestEligibilityMailTests
{
    public static void Run()
    {
        int checks=Count;
        var fields=ImmutableArray.Create(new QuestField("mail time",[new(0,0,null)],[]),
            new QuestField("mail title",[new(6,0,"系统邀请")],[]),new QuestField("mail contents",[new(6,0,"满足前置后才发一次。")],[]));
        var first=QuestEligibilityTests.Quest(100) with {MinimumLevel=50,Definition=new(fields){Job="[all]",PrerequisiteGroups=[[103]]}};
        var shared=first with {Id=101,Path="fixture/101.qst",Definition=first.Definition! with {AccountQuest=true}};
        var catalog=new QuestCatalog(new[]{first,shared,QuestEligibilityTests.Quest(103)}.ToDictionary(q=>q.Id));
        string root=Path.Combine(Path.GetTempPath(),"S5A9-quest-invitation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string path=Path.Combine(root,"game.db");
        var owners=new AccountOwners();using var owner=owners.Acquire("invitation-owner",()=>{});
        using var other=owners.Acquire("invitation-other",()=>{});
        var repository=new CharacterRepository(path,catalog);var location=new Location(1,0,100,100);
        try
        {
            var character=repository.Create(owner,new("Invited",location,location){Level=50,CompletedQuests=[103]});
            var now=DateTimeOffset.UtcNow;
            foreach(var refused in new[]{character with {Level=49},character with {CompletedQuests=[]},
                character with {CompletedQuests=[100,101,103]},character with {ActiveMainQuests=Enumerable.Range(0,30).Select(n=>new ActiveMainQuest((byte)n,(ushort)(1000+n),1)).ToArray()}})
                That(QuestEligibilityMailPlan.Prepare(refused,catalog,now).Mails.IsEmpty,"首次邀请保留等级、前置、完成及30槽容量限制");
            var plan=QuestEligibilityMailPlan.Prepare(character,catalog,now);
            That(plan.Mails.Length==2 && plan.Mails.All(m=>m.Declaration.Time==0 && m.Attachments.IsEmpty),"资格邮件使用原声明且不额外发任务奖励");
            Sql("CREATE TRIGGER invitation_abort AFTER INSERT ON quest_eligibility_mail_dispatches BEGIN SELECT RAISE(ABORT,'invitation rollback'); END");
            Reject<SqliteException>(()=>repository.CommitQuestEligibilityMail(owner,plan),"账本末段失败令邮件投递回滚");
            That(CountRows("mailbox_messages")==0 && CountRows("mailbox_audit_log")==0 && CountRows("quest_eligibility_mail_dispatches")==0,
                "失败不留下邮件、附件、审计或提前已发标记");
            Sql("DROP TRIGGER invitation_abort");
            ImmutableArray<uint> ids=[], raced=[];
            Parallel.Invoke(()=>ids=repository.CommitQuestEligibilityMail(owner,plan),
                ()=>raced=repository.CommitQuestEligibilityMail(owner,plan));
            That(ids.IsEmpty!=raced.IsEmpty,"同一账号并发提交只有一笔获得首次投递权");
            ids=ids.AddRange(raced);
            That(ids.Length==2 && ids.Distinct().Count()==2 && CountRows("quest_eligibility_mail_dispatches")==2,
                "角色一次与账号一次邀请在同一事务建立独立账本");
            That(repository.CommitQuestEligibilityMail(owner,plan).IsEmpty &&
                repository.CommitQuestEligibilityMail(owner,QuestEligibilityMailPlan.Prepare(character,catalog,now.AddDays(2))).IsEmpty,
                "原计划重试及跨日重连不重发，不以新的时间戳改写原邮件");
            var mails=repository.LoadMailbox(owner,character.Id,now.ToUnixTimeSeconds());
            That(mails.Entries.Length==2 && mails.Entries.All(m=>m.System && m.Expires is null && m.Gold==0 && m.Attachments.IsEmpty),
                "永久系统来源邀请没有金币或附件，不冒充真实玩家");
            Sql("UPDATE mailbox_messages SET state=0");
            That(repository.CommitQuestEligibilityMail(owner,plan).IsEmpty && CountRows("mailbox_messages")==2,
                "删除可见邮件后原首次资格账本仍阻止重建");
            var second=repository.Create(owner,new("SecondInvite",location,location){Level=50,CompletedQuests=[103]});
            That(repository.CommitQuestEligibilityMail(owner,QuestEligibilityMailPlan.Prepare(second,catalog,now)).Length==1 &&
                CountRows("quest_eligibility_mail_dispatches")==3,"同账号另一角色只收到角色域邀请，账号域不重复");
            var outsider=repository.Create(other,new("OtherInvite",location,location){Level=50,CompletedQuests=[103]});
            Reject<InvalidDataException>(()=>repository.CommitQuestEligibilityMail(other,plan),"其它账号不能提交原角色的邀请计划");
            That(repository.CommitQuestEligibilityMail(other,QuestEligibilityMailPlan.Prepare(outsider,catalog,now)).Length==2,
                "不同账号各自满足资格可独立获得原邀请");
            That(QuestCompletionRepositoryTests.Same(character,repository.LoadCharacter(owner,character.Slot)),
                "邀请不改角色revision、任务槽、完成位和库存");
            repository.Delete(owner,repository.LoadDeletionTarget(owner,character.Slot));
            That(CountRows("quest_eligibility_mail_dispatches WHERE account='invitation-owner' AND scope_character_id=0")==1 &&
                CountRows("quest_eligibility_mail_dispatches WHERE account='invitation-owner' AND scope_character_id="+character.Id)==0,
                "删角色只清理角色域邀请，保留账号已投递事实");
            var replacement=repository.Create(owner,new("Recreated",location,location){Level=50,CompletedQuests=[103]});
            That(repository.CommitQuestEligibilityMail(owner,QuestEligibilityMailPlan.Prepare(replacement,catalog,now)).Length==1,
                "重建角色只能收到角色域邀请，不能刷新账号域邀请");
            Console.WriteLine($"PASS {Count-checks} 首次任务资格邮件、角色/账号范围、原子回滚与跨日重放断言");
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
        void Sql(string sql){using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
        long CountRows(string table){using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM "+table;return (long)c.ExecuteScalar()!;}
    }
}
