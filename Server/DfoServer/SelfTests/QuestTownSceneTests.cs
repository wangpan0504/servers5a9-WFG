using DfoServer.Game.Quests;
using DfoServer.Game.Currency;
using DfoServer.Game.Session;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestTownSceneTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? QuestCatalog.Load(pvf); var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var parameters = QuestRewardParameterCatalog.Load(pvf);
        using (var f = Make([3155],new(38,3,300,250)))
        {
            That(Change(f,3156,QuestLifecycleAction.Accept)?.Commit is not null,"实际3156城镇范围任务接取");
            var before = f.Read();
            That(Change(f,3156,QuestLifecycleAction.Condition)?.Commit is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "同一区域但未到任务矩形不能由33完成");
            That(towns.IsPosition(new(38,3,845,250),QuestEligibility.Completed(before)) &&
                !QuestClientSceneProgress.Matches(before with { Position = new(38,3,845,250) },quests.Get(3156),towns) &&
                QuestClientSceneProgress.Matches(before with { Position = new(38,3,844,250) },quests.Get(3156),towns),
                "实际可行走的右边界按PtInRect排除，边界内一像素接受");
            That(f.Session.Move(new(600,250,0,0,0,0)),"真实合法城镇坐标进入范围");
            var terminal = Change(f,3156,QuestLifecycleAction.Condition)!;
            That(terminal.Commit is not null && terminal.Character.ActiveMainQuests.Single().ConditionState == 0,
                $"3156范围Boolean只在声明城镇、区域与矩形内接受：{terminal.Reason}，{terminal.Character.Position}");
            That(Change(f,3156,QuestLifecycleAction.Condition)!.Outcome == QuestLifecycleOutcome.Unchanged,
                "重复范围事件不产生第二笔revision");
            That(Finish(f,3156).Receipt?.Commit.ConditionType == 64 && f.Read().CompletedQuests.Contains(3156),
                "真实NPC501场景完成范围任务，分类64领奖回执同事务");
            That(f.Session.Travel(new(39,0,300,250,0,38,3,0)),"3156完成后开放实际赫顿玛尔路线");
            var accept = Change(f,3158,QuestLifecycleAction.Accept);
            That(accept?.Commit is not null && quests.Get(3158).CinematicPath is not null,"实际演出121和进入城镇39接取3158");
            before = f.Read();
            That(Finish(f,3158).Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "进入城镇或接取不能代替客户端演出终末事件");
            f.Sql("CREATE TRIGGER scene_progress_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'scene progress rollback'); END");
            Reject<SqliteException>(() => Change(f,3158,QuestLifecycleAction.Condition),"演出进度保存失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && QuestCompletionRepositoryTests.Same(before,f.Session.Selected!),
                "演出进度SQL失败不安装在线快照或写完成位");
            f.Sql("DROP TRIGGER scene_progress_abort");
            That(Change(f,3158,QuestLifecycleAction.Condition)?.Commit is not null,"同一原生演出终末事件可恢复重试");
            var result = Finish(f,3158);
            That(result.Receipt?.Commit.ConditionType == 65 && f.Read().CompletedQuests.Contains(3158) &&
                !f.Read().CompletedQuests.Contains(3159),"演出只完成自身并领取自身奖励，不自动完成下一条剧情");
            That(Change(f,3159,QuestLifecycleAction.Accept)?.Commit is not null &&
                Change(f,3159,QuestLifecycleAction.Condition)?.Commit is not null && Finish(f,3159).Outcome == QuestFinishOutcome.Committed,
                "3159卡拉卡斯会面使用直接前置演出的真实对话NPC和同一场景");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(QuestCompletionRepositoryTests.Same(f.Read(),f.Session.Selected!),"范围和演出回执及角色状态重选恢复");
        }
        using (var f = Make([3156],new(38,0,800,250)))
        {
            Change(f,3158,QuestLifecycleAction.Accept);var before = f.Read();
            That(Change(f,3158,QuestLifecycleAction.Condition)?.Commit is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "错误城镇不能完成其他区域的演出121");
        }
        using (var f = Make([3155,3156,3158,3159],new(40,0,2300,250)))
        {
            var accepted = Change(f,3160,QuestLifecycleAction.Accept)!;
            That(accepted.Commit is not null,"3160接取：" + accepted.Reason);
            var progress = Change(f,3160,QuestLifecycleAction.Condition)!;
            That(progress.Commit is not null,"3160范围：" + progress.Reason);
            var prepared = QuestCompletionPlan.Prepare(f.Read(),quests.Get(3160),ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,
                items.EpicPieces,growth,DateTimeOffset.UtcNow);
            That(prepared.Plan is not null,"3160交付计划：" + prepared.Reason);
            That(Finish(f,3160).Outcome == QuestFinishOutcome.Committed,"实际3160西海岸矩形和原生自动交付闭环");
        }
        using (var f = Make([3155,3156,3158,3159,3160,3161],new(40,3,750,250)))
        {
            var target = quests.Get(3162);
            That(target.SubType == 0 && target.IntData.SequenceEqual([13,350,250]) &&
                Change(f,3162,QuestLifecycleAction.Accept)?.Commit is not null,"3162按实际NPC范围定义正常接取");
            var before = f.Read();
            That(Change(f,3162,QuestLifecycleAction.Condition)?.Commit is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "与目标NPC相隔超过width+40时拒绝客户端终末事件");
            That(f.Session.Move(new(690,250,0,0,0,0)) &&
                Change(f,3162,QuestLifecycleAction.Condition)?.Character.ActiveMainQuests.Single().ConditionState == 0,
                "V31 NPC范围在width+40边界可达成，服务端校验真实当前坐标");
            That(Finish(f,3162).Receipt?.Commit.ConditionType == 64 && f.Read().CompletedQuests.Contains(3162),
                "3162在目标范围内由剧情临时莱诺NPC444交付，无需前往另一张常驻NPC地图");
            That(QuestEligibility.Visible(f.Read(),quests).Contains(3163),"3162完成后开放实际Lv18后继3163，不补跳其他剧情");
        }
        using (var f = Make([3155,3156,3172],new(40,0,1214,250),20))
        {
            That(Change(f,3173,QuestLifecycleAction.Accept)?.Commit is not null &&
                f.Read().ActiveMainQuests.Single().ConditionState == 514,"实际Lv20任务3173初始缺两件物品且尚未会面");
            var target = quests.Get(3173);
            That(target.IntData.SequenceEqual([10164787,1,10164789,1,29]),"3173按匹配PVF保留物品对与末尾罗杰NPC29");
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10164787,1),new(10164789,1)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted &&
                f.Read().ActiveMainQuests.Single().ConditionState == 512,"真实前置任务物品到齐后只清材料通道");
            That(f.Session.ChangeQuest(3173,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(0x20,0,0))?.Commit is not null,
                "实际罗杰场景满足3173独立NPC通道");
            var before = f.Read();
            f.Sql("CREATE TRIGGER story_material_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'story material rollback'); END");
            Reject<SqliteException>(() => Finish(f,3173),"3173材料及领奖回执末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"3173两件真实物品UID、金币经验和完成位整笔回滚");
            f.Sql("DROP TRIGGER story_material_abort");
            var finished = Finish(f,3173);
            That(finished.Receipt?.Commit is { ConditionType: 25, Consumed.Length: 2 } &&
                f.Read().Inventory.Items.All(item => item.Core.ItemId is not (10164787 or 10164789)) && f.Read().CompletedQuests.Contains(3173),
                "3173实际两件任务物品交付与自身奖励原子提交");
            That(QuestEligibility.Visible(f.Read(),quests).Contains(3174) && Finish(f,3173).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "3173交付后真实开放3174，重复请求不消耗或再次发奖");
        }
        using (var f = Make([3155,3156,3181],new(39,0,300,250),24))
        {
            var quest = quests.Get(3182);
            That(quest.CompleteNpc == -1 && QuestCoverage.Unconfirmed(quest).Length == 0 &&
                Change(f,3182,QuestLifecycleAction.Accept)?.Commit is not null,"3182职业差异文案不阻断实际自动寻物任务接取");
            var before = f.Read();
            That(Finish(f,3182).Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "无交付NPC的任务仍拒绝库存不足，不由客户端终末位代替材料");
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(3037,30),new(10165043,1)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted &&
                f.Read().ActiveMainQuests.Single().ConditionState == 0,"3182钱包晶块与实际任务物品到齐后重算进度");
            long cubes = CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.ClearCube);
            That(Finish(f,3182).Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(3182) &&
                CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.ClearCube) == cubes-30 &&
                f.Read().Inventory.Items.All(item => item.Core.ItemId != 10165043),"3182自动交付原子消费钱包与物品，无需另找常驻NPC");
            before = f.Read();
            That(Finish(f,3182).Outcome == QuestFinishOutcome.AlreadyCompleted && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "3182重复交付不再次消耗晶块或发奖");
        }
        using (var f = Make([3155,3156,3186],new(39,0,300,250),24))
        {
            var quest = quests.Get(3187);
            That(!quest.Definition!.Has("auto accept") && quest.Definition.Has("go guide") &&
                Change(f,3187,QuestLifecycleAction.Accept)?.Commit is not null,"3187演出122按真实NPC引导入口接取，不要求auto accept字段");
            var before = f.Read();
            That(Change(f,3187,QuestLifecycleAction.Condition)?.Commit is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "同一城镇的错误区域不能签发女王演出完成进度");
            f.TravelTo(towns,new(39,1,450,250));
            That(Change(f,3187,QuestLifecycleAction.Condition)?.Commit is not null &&
                Finish(f,3187).Receipt?.Commit.ConditionType == 65 && f.Read().CompletedQuests.Contains(3187) &&
                !f.Read().CompletedQuests.Contains(3188),"女王NPC148引导的演出122只完成自身，不补跳后继任务");
        }
        Console.WriteLine($"PASS {Count - checks} 实际城镇范围、演出121/122、寻物会面与自动交付及原子回滚断言（非实机）");
        QuestSceneTests.Fixture Make(int[] completed,Location position,byte level = 17)
        {
            var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,level,completed);
            try
            {
                f.TravelTo(towns,position);
                return f;
            }
            catch { f.Dispose();throw; }
        }
        QuestLifecycleReceipt? Change(QuestSceneTests.Fixture f,ushort id,QuestLifecycleAction action) => f.Session.ChangeQuest(id,action,quests,
            items.Items,items.GoldLimits,items.EpicPieces,action == QuestLifecycleAction.Condition ? new(null,0,0) : null);
        QuestFinishResult Finish(QuestSceneTests.Fixture f,ushort id) => f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,
            items.Items,items.GoldLimits,items.EpicPieces,growth);
    }
}
