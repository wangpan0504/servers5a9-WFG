using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class LevelUpByTableTests
{
    internal const int Segmented = 10303200;
    internal const int Plain = 10158963;
    internal const int WithMaterials = 490704662;
    internal const int RookieCapsule = 490704508;
    static readonly InventorySlot Slot = new(InventoryListType.Main,65);

    public static void RunCore()
    {
        int checks = Count;
        var template = new ItemTemplate(1,ItemKind.Consumable,0,1,0,99,0,4,1,"[trade]",ushort.MaxValue,0,"table");
        var raw = new StackableFile(1,"stackable/table.stk",new("[etc]",[0]),99,null,null,1,null,[])
        { DeclaredNeededMaterials=[],DeclaredDontChangeGrowTypes=[],DeclaredUsableFirstGrowTypes=[] };
        var file = new ExperienceConsumableFile(raw,new("[levelUpByTable]",[1,28,80,29,49,81,50,55,82]),null,[],["increase status type"],[]);
        var definition = LevelUpByTableDefinition.Parse(file,template)!;
        That(definition is not null && definition.TargetLevel(1)==80 && definition.TargetLevel(28)==80 &&
            definition.TargetLevel(29)==81 && definition.TargetLevel(49)==81 && definition.TargetLevel(50)==82 &&
            definition.TargetLevel(56)==0,"等级表两端均包含，不匹配区间不补成一级或最高目标");
        That(LevelUpByTableDefinition.Parse(file with { Effect=new("[levelUpByTable]",[1,30,80,20,50,85]) },template)!.TargetLevel(25)==80,
            "重叠表保留原序第一项，不排序成较高等级奖励");
        foreach (var invalid in new[] { file with { Effect=new("[levelUpByTable]",[1,28]) },
            file with { Effect=new("[levelUpByTable]",[28,1,80]) },file with { Effect=new("[levelUpByTable]",[1,28,96]) },
            file with { Effect=new("[expUpByTable]",[1,28,80]) },file with { Diagnostics=["duplicate"] },
            file with { Stackable=raw with { ActionDeclaration=new("[jumping lv]",[1]) },DeclaredFields=["action type"] } })
            That(LevelUpByTableDefinition.Parse(invalid,template) is null,"畸形区间、未知等级和额外动作不能忽略后消费");
        That(LevelUpByTableDefinition.Parse(file with { Stackable=raw with { HasActionUsablePlace=true,
            DeclaredActionUsablePlaces=["[seria room]"] } },template) is { TownOnly:true,SeriaRoomOnly:true } &&
            LevelUpByTableDefinition.Parse(file with { Stackable=raw with { HasActionUsablePlace=true,
                DeclaredActionUsablePlaces=["[seria room]","[village]"] } },template) is { TownOnly:true,SeriaRoomOnly:false },
            "地点列表为或关系，赛丽亚限定不放宽为任意城镇");
        var restricted = LevelUpByTableDefinition.Parse(file with { Stackable=raw with { DeclaredUsableFirstGrowTypes=[5] } },template)!;
        That(restricted.Allows(10,3,0x25) && !restricted.Allows(10,3,1) && !restricted.Allows(80,3,5),
            "第一转职限制忽略觉醒高位，但仍检查当前等级表和严格升级");
        Bytes(LevelUpTicketPackets.Rejected(LevelUpConsumableKind.StatusTable,Slot),[0,19,0],"CMD30失败仍用原生经验物品读宽");
        Bytes(LevelUpTicketPackets.Rejected(LevelUpConsumableKind.JumpingTable,Slot),[0,19,0,65,0,0,193],
            "515失败使用u16错误、u16槽位和u8动作，不使用418应答");
        Console.WriteLine($"PASS {Count-checks} 按表升级纯规则断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,SkillLearningCatalog skills,QuestCatalog quests)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf,skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new LevelUpTicketService(items.Items,growth,clock,characters,skills);
        foreach (int id in new[] { Segmented,Plain,WithMaterials,490704541,490704508,490703949,490703678,490703677,490702738 })
            That(items.Items.LevelUpByTable(id) is not null && items.Items.LevelUpTicket(id) is null &&
                items.Items.ExperienceConsumable(id) is null,"真实名册按表升级不会误入418或普通经验增量: "+id);
        That(items.Items.LevelUpByTable(RookieCapsule) is { Jumping:true,SeriaRoomOnly:true,Ranges:[{ Minimum:1,Maximum:17,Target:85 }] },
            "新秀胶囊使用真实Lv1至17升85表及赛丽亚房间限制，不借其他分段胶囊替代");
        foreach (byte level in new byte[] {1,17})
        using (var f = Make(level,RookieCapsule))
        {
            var used = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1)!;
            That(used.Commit is not null && used.Character.Level==85 && used.Character.GrowType==1 &&
                used.Character.Learning.Experience==growth.GetLevelStartExperience(85) && used.Character.Inventory.Find(Slot) is null &&
                Same(used.Character,f.Read()),"新秀胶囊两端等级均同事务转职、到85级0%并消费唯一源");
        }
        foreach (var (level,street,selection) in new (byte,bool,uint)[] { (18,false,1),(17,true,1),(17,false,0) })
        using (var f = Make(level,RookieCapsule,street:street))
        {
            var before = f.Read();
            That(f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,selection)!.Commit is null &&
                Same(before,f.Read()),"新秀胶囊超等级、房间外及取消转职均不吞物品或改变成长");
        }
        using (var f = Make(27,Segmented,count:2))
        {
            var before = f.Read();
            var first = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1)!;
            var after = first.Commit!.Character;
            That(after.Level==80 && after.GrowType==1 && after.Learning.Experience==growth.GetLevelStartExperience(80) &&
                after.Learning.BonusSp==17 && after.Learning.BonusTp==5 && after.Inventory.Find(Slot)!.Core.Count==1 &&
                after.CompletedQuests.Contains(100) && !after.CompletedQuests.Contains(103) && after.Revision==before.Revision+1 && Same(after,f.Read()),
                "分段胶囊升级至目标0%，首次转职、两树、主线跳过和一件源同事务提交");
            Bytes(LevelUpTicketPackets.Used(first.Commit.Plan.LevelUpTicket!),[1,65,0,0,193,0,0,0],
                "jumping消费ACK只带193，不追加旧端经验或职业字段");
            That(f.Repository.CommitQuestQuickClear(f.Owner,first.Commit.Plan) is { Duplicate:true,ProjectionCurrent:true } && Same(after,f.Read()),
                "精确回执重放不重新扣券、返点或改职业");
            f.Session.MarkQuestTicketAcknowledged(first);
            var second = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable)!;
            That(second.Character.Level==86 && second.Character.GrowType==1 && second.Character.Inventory.Find(Slot) is null,
                "合法再次使用按新等级重新选表，已转职不重复重建或返还技能点");
        }
        using (var f = Make(29,Plain))
        {
            var before = f.Read();
            var source = service.PrepareTable(before,Slot,LevelUpConsumableKind.StatusTable,0);
            That(source.Growth.TargetLevel==81 && source.Growth.GainedExperience==growth.GetLevelStartExperience(81)-before.Learning.Experience &&
                source.ProfessionGrowType is null,"无jumping动作仅按表升级，不强制替未转职角色选择职业");
            var result = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.StatusTable)!;
            That(result.Commit is not null && result.Character.Level==81 && result.Character.GrowType==0,
                "CMD30等级表消费、成长及主线仍走同一个已提交回执");
        }
        using (var f = Make(10,Segmented))
        {
            var before = f.Read();
            var source = service.PrepareTable(before,Slot,LevelUpConsumableKind.JumpingTable,1);
            That(QuestQuickClearPlan.TryCreateLevelUpTicket(before,Guid.NewGuid(),quests,source,
                ImmutableDictionary<int,byte>.Empty,out var plan,out _,towns) && plan is { TargetLevel:80,ProfessionGrowType:1 },
                "真实完整任务目录的大幅升级范围可规划，未答分支保留原生等待而非默认全清");
        }
        foreach (byte job in new byte[] {9,10})
        using (var f = Make(10,Segmented,job:job))
        {
            var source = service.PrepareTable(f.Read(),Slot,LevelUpConsumableKind.JumpingTable,0);
            That(source.Growth.TargetLevel==80 && source.ProfessionGrowType is null && source.Allows(f.Read()),
                "黑暗武士和缔造者沿193直接升级，不要求不存在的首次转职选择");
        }
        using (var f = Make(29,Plain,street:true))
        {
            var before = f.Read();
            That(!towns.IsRoom(before.Position) && f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.StatusTable)!.Commit is null &&
                Same(before,f.Read()),"仅赛丽亚房间的表道具不在普通街道消费");
        }
        foreach (var (level,id,grow,target) in new (byte,int,byte,byte)[] { (86,490704541,1,95),(70,490703949,5,95),(10,490703677,0,95) })
        using (var f = Make(level,id,grow))
        {
            var source = service.PrepareTable(f.Read(),Slot,LevelUpConsumableKind.JumpingTable,grow==0 ? 1u : 0u);
            That(source.Growth.TargetLevel==target && source.Growth.GainedExperience>0 && source.Growth.GainedExperience<=uint.MaxValue,
                "直接95使用真实64位累计差额，不在原经验上再加目标总经验");
        }
        using (var f = Make(10,WithMaterials,materials:101))
        {
            var result = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1)!;
            That(result.Commit is not null && result.Character.Level==95 && result.Character.Inventory.Find(Slot) is null &&
                result.Character.Inventory.Items.Single(item=>item.Core.ItemId==490704642).Core.Count==101,
                "使用已持有胶囊只扣一件源，不重复扣商店兑换所需100份凭证");
        }
        using (var f = Make(10,WithMaterials))
            That(f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1)!.Character.Level==95,
                "任意合法途径取得的胶囊使用时无需再持有商店兑换材料");
        foreach (var (id,level,grow,selection) in new (int,byte,byte,uint)[]
            { (Segmented,10,0,0),(Segmented,10,1,2),(490703949,70,1,0),(490704541,84,1,0) })
        using (var f = Make(level,id,grow))
        {
            var before = f.Read();
            That(f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,selection)!.Commit is null && Same(before,f.Read()),
                "未选择转职、伪造已转职业、第一分支或等级不符均保留资产");
        }
        using (var f = Make(10,Segmented,definitions:LevelUpTicketTests.BranchQuests()))
        {
            var before = f.Read();
            var pending = f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1)!;
            That(pending.Prepared is { PendingQuestions:[100] } && Same(before,f.Read()),"主线问题未答前不先升级或转职");
            var result = f.Session.AnswerManualQuestSkip([new(100,1)],f.Catalog)!;
            That(result.Commit is not null && result.Character.Level==80 && result.Character.GrowType==1 &&
                result.Commit.Plan.LevelUpTicket?.Kind==LevelUpConsumableKind.JumpingTable && result.Character.CompletedQuests.Contains(102),
                "回答后恢复原193入口、职业选择和等级表，不降为418或无道具手册");
        }
        using (var f = Make(10,Segmented))
        {
            var before = f.Read();
            f.Sql("CREATE TRIGGER level_table_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'level table rollback'); END");
            TestCheck.Reject<SqliteException>(()=>f.Session.UseLevelUpTicket(Slot,service,f.Catalog,LevelUpConsumableKind.JumpingTable,1),"末段SQL失败");
            That(Same(before,f.Read()) && Same(before,f.Session.Selected!),"失败回滚等级、转职、两树、源与任务，不安装部分内存");
            f.Sql("DROP TRIGGER level_table_abort");
        }
        Console.WriteLine($"PASS {Count-checks} 按表升级资源、职业、主线及事务断言");

        QuestOrdinaryCompletionTests.Fixture Make(byte level,int id,byte grow=0,int count=1,int materials=0,StoryQuest[]? definitions=null,
            byte job=0,bool street=false) =>
            new(towns,characters,skills,growth,parameters,items,QuestEligibilityTests.Quest(100),materialCount:0,
                active:[],definitions:definitions ?? LevelUpTicketTests.PlainQuests(),resumeTown:street,configure:candidate=>
                {
                    if (job!=0) candidate=CharacterCreation.Prepare(candidate.Name,characters.Get(job),towns.Room,towns.InitialReturn,skills.Get);
                    var inventory = candidate.Inventory.Items.Add(new(0,Slot,new(id,ItemKind.Consumable,count)));
                    if (materials>0)
                    {
                        var material = items.Items.Get(490704642);
                        short index = checked((short)(ItemSlotBoundService.Range(material.Kind,candidate.Inventory.Expansion).Start+1));
                        inventory = inventory.Add(new(0,new(InventoryListType.Main,index),new(material.Id,material.Kind,materials)));
                    }
                    return candidate with
                    {
                        Position=street ? towns.InitialReturn : candidate.Position,
                        Level=level,GrowType=grow,InitialLearning=SkillPointLedger.Synchronize(job,grow,level,candidate.InitialLearning with
                            { Experience=growth.GetLevelStartExperience(level)+123,BonusSp=17,BonusTp=5 },skills),
                        Inventory=candidate.Inventory with { Items=inventory }
                    };
                });
    }
}
