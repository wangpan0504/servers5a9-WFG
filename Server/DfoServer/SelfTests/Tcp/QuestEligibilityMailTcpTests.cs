using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class QuestEligibilityMailTcpTests
{
    public static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        await using var f=new TcpTestFixture(source,Path.Combine(root,"quest-eligibility-mail"),resources:resources);
        const string account="quest-invitation-tcp";
        var owners=new AccountOwners();var repo=new CharacterRepository(f.Database,resources.Quests);
        Character created;
        using(var owner=owners.Acquire(account,()=>{}))
        {
            var candidate=CharacterCreation.Prepare("InviteTCP",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var growth=SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            created=repo.Create(owner,candidate with {Level=50,AcceptedStoryLevel=50,
                InitialLearning=SkillPointLedger.Synchronize(0,0,50,candidate.InitialLearning with
                    {Experience=growth.GetLevelStartExperience(50)},resources.Learning)});
        }
        var declaration=QuestMailPlan.Read(resources.Quests.Get(2670));
        That(declaration.Time==0 && declaration.Title=="爱莎的问候" && declaration.Enclosures.IsEmpty,
            "真实2670时机0邀请不包含附件或伪造训练完成");
        await f.StartAsync();
        uint mailId=0;
        for(int attempt=0;attempt<2;attempt++)
        {
            using var peer=Open(f,account);peer.Select();
            using(var owner=owners.Acquire(account,()=>{}))
            {
                var page=repo.LoadMailbox(owner,created.Id,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                That(page.Entries is [var letter] && letter.System && letter.SenderName=="系统任务" &&
                    letter.Text==declaration.Body && letter.Expires is null && letter.Gold==0 && letter.Attachments.IsEmpty,
                    "登录实际NOTI97对应原标题正文的永久系统邀请");
                if(attempt==0) mailId=page.Entries.Single().Id;
                That(page.Entries.Single().Id==mailId,"重连回放同一邮件ID，不重新投递");
                MailboxProtocolTests.CheckList(peer.Mailbox,page.Entries,page.NotLoaded,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            That(f.Query("SELECT quest_id,scope_character_id,mail_id FROM quest_eligibility_mail_dispatches WHERE account=$p0",account)
                is [[2670L,long scope,long stored]] && scope==created.Id && stored==mailId,
                "真实首次资格事务只建立2670的当前角色账本");
            That(f.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id BETWEEN 2670 AND 2679",created.Id).Length==0 &&
                f.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id BETWEEN 2670 AND 2679",created.Id).Length==0,
                "邀请不自动接取或完成PVP链，不伪造训练结果");
            peer.Barrier();peer.Send(3,[0]);peer.Expect(1,3,[1]);peer.Closed();
        }
        await f.StopAsync();
    }
}
