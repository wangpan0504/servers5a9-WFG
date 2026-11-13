using System.Collections.Immutable;
using DfoServer.Game.Auction;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Quests;
using DfoServer.Game.Settings;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestTownMutationPendingTests
{
    const int Material = 10014751;

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var ordinary = QuestEligibilityTests.Quest(100) with { Condition = "[seeking]", IntData = [10165055,1],
            Rewards = [new(0,2058,null),new(0,1,null)], Attributes = ["not give exp quest"] };
        var question = QuestEligibilityTests.Quest(400) with { Condition = "[question]", Definition = new([
            new("question info",[new(6,0,"标题"),new(6,0,"正文"),new(6,0,"一"),new(6,0,"二")],[])]) };
        // 问题需有真实普通回答后继，缓存对照才能执行最终1431提交。
        var successor = QuestEligibilityTests.Quest(500) with { Definition = new([]) { RequiredAnswers = [new(400,0)] } };
        StoryQuest[] definitions = [ordinary,QuestEligibilityTests.Quest(101),question,successor,
            QuestEligibilityTests.Quest(999) with { Grade = "[invisible]", IntData = [-1],
                Definition = new([new("synchronization account quest",[new(0,1,null)],[])]) }];
        var auctionItems = new ItemCatalog(null!,null!,null!,0);
        auctionItems.PrefillTemplates(new Dictionary<int,ItemTemplate>
        {
            [Material] = new(Material,ItemKind.Material,0,1,0,999,1,0,1,"[free]",65535,0,"测试材料")
        });
        var reset = new CharacterProgressReset(characters,learning,growth);
        foreach (bool lostReturn in new[] { false,true })
        {
            using var f = Make();
            var service = new AuctionService(f.Repository,new AuctionCategoryResolver(auctionItems),auctionItems,items.EpicPieces,
                AuctionPackets.ItemEntrySnapshot,AuctionPackets.ItemCoreFromEntry);
            var other = f.Repository.Create(f.Owner,Candidate("Other"));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var request = new AuctionRegisterRequest(0,0,121,Material,1,100,100,100,24);
            var initial = f.Session.Selected!;
            That(f.Session.RegisterListing(service,initial,service.PlanRegister(initial,request,now).Options!).Listing.Error == AuctionRegisterError.None,
                "预置真实自有挂牌，随后取消计划确实会返包");
            That(service.Register(f.Owner,other,service.PlanRegister(other,request,now).Options!).Error == AuctionRegisterError.None,
                "预置另一角色真实挂牌，随后购买计划确实会扣款");
            var expirySource = f.Session.Selected!;
            That(f.Session.RegisterListing(service,expirySource,service.PlanRegister(expirySource,request,now-172800).Options!).Listing.Error == AuctionRegisterError.None,
                "预置真实已到期自有挂牌，用于验证托管结算独立性");
            var mine = f.Repository.LoadSellerListings(initial.Id,now,false).Single();
            var sale = f.Repository.LoadSellerListings(other.Id,now,false).Single();
            var before = f.Session.Selected!;
            var register = service.PlanRegister(before,request,now).Options!;
            var cancel = service.PlanCancel(before,mine,new(0,mine.ListingId,mine.ExpiresAtUnix),now).Options!;
            var buy = service.PlanBuy(before,sale,new(0,100,sale.ListingId,checked((uint)sale.ExpiresAtUnix),Material),now).Options!;
            That(register is not null && cancel is not null && buy is not null,"三条拍卖规划在未决任务之前均合法");
            var expiry = service.PlanExpire(before,now);
            var settings = Settings(f);
            var options = new byte[ClientSettingsService.GameOptionLength].ToImmutableArray();
            var mailbox = new MailboxService(f.Repository,items.Items,items.GoldLimits,items.EpicPieces);
            var recipient = new MailboxRecipient(before.Id,f.Owner.Account,before.Name,before.Job,before.GrowType,before.Level);
            var mail = mailbox.Deliver(new("pending-town-test",recipient,"系统","独立邮件",20,[],now));
            That(mail.Accepted && mailbox.PlanSend(before,f.Owner.Account,new(other.Name,1,[],"正文"),DateTimeOffset.UtcNow,out _) == MailboxError.None &&
                mailbox.PlanClaim(f.Owner,before,[mail.MailId],DateTimeOffset.UtcNow,out _) == MailboxError.None,
                "寄送和领取请求预先具备真实资产规划资格");
            if (!lostReturn)
                f.Sql("CREATE TRIGGER town_pending_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'town pending rollback'); END");
            if (lostReturn) TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"真实领奖提交后返回丢失");
            else TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"真实领奖末段回滚后保留原计划");
            var persisted = f.Read();
            That(ReferenceEquals(before,f.Session.Selected) && (lostReturn ? f.Receipts == 1 : Same(before,persisted) && f.Receipts == 0),
                "区分返回丢失和SQL回滚；后者数据库仍是可被普通写入修改的旧revision");
            That(f.Session.RegisterListing(service,before,register!).Listing.Error == AuctionRegisterError.StalePlan &&
                f.Session.CancelListing(service,before,cancel!).Listing.Error == AuctionCancelError.StalePlan &&
                f.Session.BuyListing(service,before,buy!).Buy.Error == AuctionBuyError.StalePlan &&
                service.FindListing(mine.ListingId)!.State == AuctionListingState.Active &&
                service.FindListing(sale.ListingId)!.State == AuctionListingState.Active,
                "未决任务挡住真实上架/返包取消/购买计划，不改变挂牌或托管资产");
            That(f.Session.SendMail(mailbox,new(other.Name,1,[],"正文")).Error == MailboxError.InvalidState &&
                f.Session.ClaimMail(mailbox,[mail.MailId]).Error == MailboxError.InvalidState,
                "未决任务拒绝寄送与领取资产");
            That(f.Session.OpenMailbox(mailbox).Page.Entries.Any(row => row.Id == mail.MailId) &&
                f.Session.QueryMailRecipient(mailbox,other.Name).Accepted &&
                f.Session.ChangeMailState(mailbox,[mail.MailId],2).Accepted,
                "邮件查询和已读状态仍可进行，不替换任务角色快照");
            That(expiry.Rows.Length == 1 && f.Session.SettleExpiredListings(service,before,expiry).SettledCount == 1 &&
                ReferenceEquals(before,f.Session.Selected),"到期挂牌退回托管邮件继续允许，不改当前角色资产快照");
            var slot = before.Learning.Trees[0].Skills.First(value => value.Id is < 200 or > 211).Slot;
            That(f.Session.LearnSkills(new(0,0,[new(before.Learning.Trees[0].Skills[0].Id,false,1)]),learning).Failure == SkillLearningFailure.Unsupported &&
                f.Session.MoveSkillSlot(new(0,slot,slot,-1,0),learning).Failure == SkillLearningFailure.Unsupported &&
                f.Session.ResetSkills(0,0,learning).Failure == SkillLearningFailure.Unsupported &&
                f.Session.CustomizeSkills([],true,learning).Failure == SkillLearningFailure.Unsupported &&
                f.Session.CustomizeCombo(0,[],learning).Failure == SkillLearningFailure.Unsupported &&
                f.Session.BeginAutoCombo(0, learning).Failure == SkillLearningFailure.Unsupported,
                "技能学习、布局及共用定制入口拒绝未决任务");
            TestCheck.Reject<InvalidOperationException>(() => f.Session.InitializeLearning(learning),"技能初始化提交检查保留异常合同");
            That(f.Session.ChangeEmotion(settings,1) is { Error: 3, Character: null } &&
                !f.Session.SaveGameOptions(settings,options) && settings.Load(f.Owner,before.Id).GameOptions.IsDefault &&
                f.Session.AcceptStoryDigest() is null,"表情、显示设置与剧情接受不提前保存或伪报成功");
            // 模拟先前已有设置：回放读取允许，但不能为可见性替换冻结的Selected引用。
            var loaded = f.Session.LoadClientSettings(new ClientSettingsService(new ReadSettings(options)));
            That(loaded.VisibilityFlags != before.VisibilityFlags && ReferenceEquals(before,f.Session.Selected) &&
                !f.Session.CanPublishClientSettings(loaded),"设置回放只读，不安装与冻结计划不一致的显示快照");
            That(f.Session.SaveHotkeys(settings,[.. "I01\t77\n"u8.ToArray()]) &&
                f.Session.SaveCharacterOptions(settings,new byte[ClientSettingsService.CharacterOptionLength].ToImmutableArray()),
                "不影响角色快照的键位及角色选项独立保存仍允许");
            TestCheck.Reject<InvalidOperationException>(() => f.Session.SetCharacterGold(before.Name,1),"GM当前角色金币被挡");
            TestCheck.Reject<InvalidOperationException>(() => f.Session.UpdateProgress(Edit(before.Name,CharacterProgressField.Story,1)),"GM当前进度被挡");
            TestCheck.Reject<InvalidOperationException>(() => f.Session.ResetProgress(Edit(before.Name,CharacterProgressField.Level,21),reset),"GM当前成长重置被挡");
            TestCheck.Reject<InvalidOperationException>(() => f.Session.UpdateProgress(Edit(other.Name,CharacterProgressField.ExpertContract,100)),
                "非当前角色达人契约实际修改账号共享状态，仍须挡住");
            That(f.Session.SetCharacterGold(other.Name,123).Inventory.Gold == 123 &&
                f.Session.UpdateProgress(Edit(other.Name,CharacterProgressField.Story,1)).AcceptedStoryLevel == 1 &&
                f.Session.ResetProgress(Edit(other.Name,CharacterProgressField.Level,21),reset).Level == 21,
                "其他角色独立金币、进度和成长重置保持可用");
            That(ReferenceEquals(before,f.Session.Selected) && Same(persisted,f.Read()),"全部拒绝与独立操作后原任务快照和持久角色不变");
            if (!lostReturn) f.Sql("DROP TRIGGER town_pending_abort");
            var recovered = f.Finish(ushort.MaxValue);
            That(recovered.Receipt?.Commit is { ProjectionCurrent: true } commit && commit.Duplicate == lostReturn &&
                f.Lost!.SamePlan && f.Receipts == 1 && Same(f.Read(),f.Session.Selected!),"原冻结领奖精确恢复一次，失败类型不混淆");
            That(f.Session.ChangeEmotion(settings,1).Error == 0 && f.Session.AcceptStoryDigest() is not null &&
                f.Session.MoveSkillSlot(new(0,slot,slot,-1,0),learning).Success &&
                f.Session.SetCharacterGold(before.Name,10000).Inventory.Gold == 10000,
                "恢复后原本合法的设置、技能、剧情和GM写入重新允许");
            var current = f.Session.Selected!;
            That(f.Session.RegisterListing(service,current,service.PlanRegister(current,request,now).Options!).Listing.Error == AuctionRegisterError.None,
                "恢复后真实上架提交重新允许");
        }
        // 同步定义回调精确控制capture与commit间隙，无线程、睡眠或超时推断。
        using (var f = Make())
        {
            var before = f.Session.Selected!;
            var skill = before.Learning.Trees[0].Skills.First(value => value.Id is < 200 or > 211);
            f.Sql("CREATE TRIGGER town_planning_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'skill planning rollback'); END");
            int calls = 0;
            TestCheck.Reject<InvalidOperationException>(() => f.Session.MoveSkillSlot(new(0,skill.Slot,skill.Slot,-1,0),(job,id) =>
            {
                if (calls++ == 0) TestCheck.Reject<SqliteException>(() => f.Change(101,QuestLifecycleAction.Accept),"技能规划期间建立未决接取");
                return learning.Get(job,id);
            }),"技能提交再次检查新出现的未决任务，不依赖Selected变化");
            That(calls > 0 && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),"规划间隙拒绝保持原引用及数据库");
            f.Sql("DROP TRIGGER town_planning_abort");
            That(f.Change(101,QuestLifecycleAction.Accept)?.Commit is not null &&
                f.Session.MoveSkillSlot(new(0,skill.Slot,skill.Slot,-1,0),learning).Success,"原接取恢复后同槽技能请求恢复正常");
        }
        foreach (bool answered in new[] { false,true })
        using (var f = Make(active: []))
        {
            var prepared = f.Session.PrepareManualQuestSkip(f.Catalog)!;
            That(prepared.Prepared?.PendingQuestions.SequenceEqual([400]) == true,"城镇缓存夹具先准备可达问题");
            if (answered) That(f.Session.AnswerManualQuestSkip([new(400,0)],f.Catalog)?.Commit is not null,"已安装回答缓存对照");
            var settings = Settings(f);
            That(f.Session.ChangeEmotion(settings,1).Error == 0 &&
                f.Session.SaveGameOptions(settings,new byte[ClientSettingsService.GameOptionLength].ToImmutableArray()) &&
                f.Session.AcceptStoryDigest() is not null && f.Session.SetCharacterGold("Ordinary",123).Inventory.Gold == 123,
                "未提交手册及已安装回答缓存不挡普通城镇写入");
            That(!f.Session.CanPublishQuestQuickClear(prepared) && f.Session.FinishManualQuestSkip() is null,
                "手册快照随普通写入自然失效，不误恢复旧计划");
        }
        Console.WriteLine($"PASS {Count-checks} 任务未决提交与城镇写入边界断言");

        CharacterProgressEdit Edit(string name,CharacterProgressField field,int value) => new(field,"ordinary-quest",name,value,0);
        NewCharacter Candidate(string name)
        {
            var candidate = CharacterCreation.Prepare(name,characters.Get(0),towns.Room,towns.InitialReturn,learning.Get);
            return Configure(candidate);
        }
        NewCharacter Configure(NewCharacter candidate) => candidate with
        {
            Level = 20,
            InitialLearning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(20) },learning),
            Inventory = candidate.Inventory with { Gold = 100000, Items = candidate.Inventory.Items.Add(
                new(0,new(InventoryListType.Main,121),new ItemCore(Material,ItemKind.Material,5))) }
        };
        QuestOrdinaryCompletionTests.Fixture Make(ActiveMainQuest[]? active = null) => new(towns,characters,learning,growth,parameters,items,
            ordinary,definitions:definitions,active:active,loseResult:true,configure:Configure);
    }

    sealed class ReadSettings(ImmutableArray<byte> options) : IClientSettingsStore
    {
        public bool SaveSeenNotice(AccountOwners.Lease owner, byte id) => throw new NotSupportedException("此设置替身不保存提示");
        public ClientSettingsSnapshot Load(AccountOwners.Lease owner,ushort characterId) => new(options,default);
        public bool SaveGameOptions(AccountOwners.Lease owner,ImmutableArray<byte> payload,ushort? characterId = null,
            ImmutableArray<byte> characterGameOptions = default) => throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveCharacterOptions(AccountOwners.Lease owner,ushort characterId,ImmutableArray<byte> payload) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveHotkeys(AccountOwners.Lease owner,ushort characterId,byte profile,ImmutableArray<byte> text) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveQuickChat(AccountOwners.Lease owner,byte bank,ImmutableArray<byte> text) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveChatEmoticons(AccountOwners.Lease owner,ImmutableArray<ChatEmoticonMacro> macros) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveRecentEmoticons(AccountOwners.Lease owner,ImmutableArray<byte> entries) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveQuestNotify(AccountOwners.Lease owner,ushort characterId,ImmutableArray<byte> entries) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveNpcBookmarks(AccountOwners.Lease owner,ushort characterId,ImmutableArray<byte> entries) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool SaveEmotion(AccountOwners.Lease owner,ushort characterId,ushort emotion) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
        public bool? SaveCloneTitle(AccountOwners.Lease owner,ushort characterId,uint itemId,long? expiresAt = null) =>
            throw new InvalidOperationException("只读设置替身不接受保存");
    }

    static ClientSettingsService Settings(QuestOrdinaryCompletionTests.Fixture f)
    {
        using var db = f.Open();
        return new(new ClientSettingsRepository(db.DataSource),new CharacterEmotionCatalog([new(1,0,true)]));
    }
}
