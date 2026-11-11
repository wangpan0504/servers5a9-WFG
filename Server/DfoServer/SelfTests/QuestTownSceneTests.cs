using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Currency;
using DfoServer.Game.Session;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
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
        var quests = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var skills = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
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
            That(terminal.Commit is not null && terminal.Character.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests.Select(row => row.QuestId == 3156 ? row with { ConditionState = 0 } : row)),
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
                Change(f,3162,QuestLifecycleAction.Condition)?.Character.ActiveMainQuests.SequenceEqual(
                    before.ActiveMainQuests.Select(row => row.QuestId == 3162 ? row with { ConditionState = 0 } : row)) == true,
                "匹配客户端 NPC范围在width+40边界可达成，服务端校验真实当前坐标");
            That(Finish(f,3162).Receipt?.Commit.ConditionType == 64 && f.Read().CompletedQuests.Contains(3162),
                "3162在目标范围内由剧情临时莱诺NPC444交付，无需前往另一张常驻NPC地图");
            That(QuestEligibility.Visible(f.Read(),quests).Contains(3163),"3162完成后开放实际Lv18后继3163，不补跳其他剧情");
        }
        using (var f = Make([3155,3156,3172],new(40,0,1214,250),20))
        {
            var target = quests.Get(3173);
            var beforeAcceptance = f.Read();
            That(Change(f,3173,QuestLifecycleAction.Accept)?.Commit is not null &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3173).ConditionState == 513 &&
                f.Read().ActiveMainQuests.Where(row => row.QuestId != 3173).SequenceEqual(beforeAcceptance.ActiveMainQuests) &&
                QuestLifecyclePlan.MissingMaterials(f.Read().Inventory,target,DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == 2,
                "实际Lv20任务3173保留完整两件材料缺额，初值分别标记材料不足与尚未会面");
            That(target.IntData.SequenceEqual([10164787,1,10164789,1,29]),"3173按匹配PVF保留物品对与末尾罗杰NPC29");
            var beforeMaterials = f.Read();
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10164787,1),new(10164789,1)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted &&
                f.Read().ActiveMainQuests.SequenceEqual(beforeMaterials.ActiveMainQuests.Select(row => row.QuestId == 3173 ? row with { ConditionState = 512 } : row)),"真实前置任务物品到齐后只清材料通道");
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
                f.Read().ActiveMainQuests.SequenceEqual(before.ActiveMainQuests.Select(row => row.QuestId == 3182 ? row with { ConditionState = 0 } : row)),"3182钱包晶块与实际任务物品到齐后重算进度");
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
        using (var f = Make([3155,3156,3181,3223,3248,3609],new(42,2,788,222),95))
        {
            That(quests.Get(3249).IntData.SequenceEqual([3609,3610]) &&
                QuestRelationProgress.ZeroRewardChild(quests.Get(3609)) && QuestRelationProgress.ZeroRewardChild(quests.Get(3610)),
                "实际梅娅女王父子定义使用禁经验和0/0物品占位");
            That(Change(f,3249,QuestLifecycleAction.Accept)?.Commit is not null &&
                f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                    [new(10164786,1)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted &&
                Change(f,3610,QuestLifecycleAction.Accept)?.Commit is not null,
                "按真实前置、父任务和信函库存接取末个寻物会面子任务");
            var changed = f.Session.ChangeQuest(3610,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,
                items.EpicPieces,new(0x20,0,0));
            That(changed?.Commit is not null && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3610).ConditionState == 0,
                "实际NPC303场景接受与现场相同的20通道归零请求");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var before = f.Read();
            f.Sql("CREATE TRIGGER maya_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'linked rollback'); END");
            Reject<SqliteException>(() => Close(),"已归零梅娅子任务自动交付末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"信函、子完成位和父条件整笔回滚，保留原归零接取");
            f.Sql("DROP TRIGGER maya_abort");
            var closed = Close();
            var after = f.Read();
            // 重选可合法补入地区每日；只移除本子任务并归零父任务，其余接取逐字段保持。
            var expectedActive = before.ActiveMainQuests.Where(row => row.QuestId != 3610)
                .Select(row => row.QuestId == 3249 ? row with { ConditionState = 0 } : row).ToArray();
            That(closed?.Commit is { ExperienceReward.Amount: 0, Rewards.Length: 0, Consumed.Length: 1, GoldAmount: 0 } &&
                after.Inventory.Gold == before.Inventory.Gold &&
                after.ActiveMainQuests.SequenceEqual(expectedActive) &&
                after.ActiveMainQuests.Any(row => row.QuestId == 3249 && row.ConditionState == 0) &&
                after.CompletedQuests.Contains(3610) && !after.CompletedQuests.Contains(3249) &&
                after.Inventory.Items.All(item => item.Core.ItemId != 10164786),
                "重选恢复已归零子任务，真实消费一次信函并更新父条件，不自动发父奖励；活动任务="+
                string.Join(",",after.ActiveMainQuests.Select(row => $"{row.QuestId}:{row.ConditionState}")));
            var saved = f.Read();
            That(Close() is null && QuestCompletionRepositoryTests.Same(saved,f.Read()),"重复恢复不再扣信函或保存完成回执");
            That(Finish(f,3249).Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(3249),
                "梅娅女王父任务按原34正式交付完成");
            QuestCompletionReceipt? Close() => f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth);
        }
        foreach (bool hasLetter in new[] { false,true })
        using (var f = Make([3155,3156,3181,3223,3248,3249,3250,3609],new(42,0,300,250),95))
        {
            if (hasLetter)
                That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                    [new(10164786,1)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted,"历史残留信函按正常库存owner准备");
            f.Sql("INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) SELECT id,5,3610,0,'8d9a7f7bb6d94979b1ab3f519b90a652' FROM characters");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var before = f.Read();
            f.Sql("CREATE TRIGGER orphan_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'orphan rollback'); END");
            Reject<SqliteException>(() => Close(),"历史孤立子任务归档末段失败整笔回滚");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"历史恢复失败保留父完成位、原子接取和全部资产");
            f.Sql("DROP TRIGGER orphan_abort");
            var closed = Close();
            var saved = f.Read();
            That(closed?.Commit is { ExperienceReward.Amount: 0, Rewards.Length: 0, Consumed.Length: 0, GoldAmount: 0 } &&
                saved.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests.Where(row => row.QuestId != 3610)) &&
                saved.CompletedQuests.SequenceEqual(before.CompletedQuests.Append(3610).Order()) &&
                saved.Revision == before.Revision+1 && QuestCompletionRepositoryTests.Same(before with
                    { ActiveMainQuests = saved.ActiveMainQuests, CompletedQuests = saved.CompletedQuests, Revision = saved.Revision },saved),
                "复现111父3249已完成、子3610归零：只补子归档一次，不重新扣信函或发父子奖励");
            That(Close() is null && Finish(f,3610).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(saved,f.Read()),"重选和重复34不再次写入历史恢复");
            QuestCompletionReceipt? Close() => f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth);
        }
        using (var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,[3155]))
        foreach (var child in quests.All.Where(q => q.RewardType == "[item]" && QuestRelationProgress.ZeroRewardChild(q) &&
            q.MainQuest is { } parentId && quests.TryGet(parentId,out var parent) && QuestRelationProgress.SequentialParent(parent,quests))
            .GroupBy(q => q.Condition).Select(group => group.First()))
        {
            var expected = f.Read() with { CompletedQuests = [.. f.Read().CompletedQuests,child.MainQuest!.Value],
                ActiveMainQuests = [new(5,checked((ushort)child.Id),0) { AcceptanceId = Guid.NewGuid() }] };
            var prepared = QuestCompletionPlan.Prepare(expected,child,ushort.MaxValue,quests,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
            That(prepared.Plan is { Inventory.Changed: false, ExperienceReward.Amount: 0, GoldAmount: 0, Consumed.Length: 0 } plan &&
                plan.CompletedParentId == child.MainQuest && plan.AuthoritativeClear is null,
                "实际同类"+child.Condition+"终末子归档无需重造清图或材料，保留父完成位与全部资产");
            That(QuestCompletionPlan.Prepare(expected with { ActiveMainQuests = [expected.ActiveMainQuests[0] with { ConditionState = 1 }] },
                child,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is null,
                "同类历史归档不代替未达成的"+child.Condition+"条件");
        }
        RunSmanyRangeRecovery(towns,pvf,characters,items,quests,skills);
        DungeonCinematic();
        Console.WriteLine($"PASS {Count - checks} 实际城镇范围、城镇/地下城演出、寻物会面与交付及原子回滚断言（非实机）");

        void DungeonCinematic()
        {
            // 真实3599/2406/96全部资源装配；只走基础门墙到76114，不遍历76113叠层演出。
            // 合成终末33与前序敌对击杀只验证进度事务，不代表完整路线、播放或dummy退场结算。
            var quest = quests.Get(3599);
            var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
            var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
            That(QuestClientSceneProgress.DungeonCinematic(quest) && quest.IntData is [2406] && quest.CinematicMapId == 76114,
                "紧急会议保持真实地下城演出资源，不改写普通清图目标");
            var area = towns.Areas.Values.First(row => row.Level <= 74 && row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 96 &&
                    (option.QuestId == -1 || option.InProgress && option.QuestId == 3599 || !option.InProgress && option.QuestId != 3599)));
            var done = new HashSet<int>(area.Quests) { 3598 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            var option = towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 96 &&
                (row.QuestId == -1 || row.InProgress && row.QuestId == 3599 || !row.InProgress && row.QuestId != 3599));
            if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
            foreach (int id in area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3599)).Quests)
                done.Add(id);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,74,done.ToArray(),position:position);
            That(Change(f,3599,QuestLifecycleAction.Condition)?.Commit is null,"未接取不能用演出33建任务进度");
            That(Change(f,3599,QuestLifecycleAction.Accept)?.Commit is not null &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 1,
                "3599正常接取初值1，接取不能替代终末通知");
            That(Change(f,3599,QuestLifecycleAction.Condition)?.Commit is null && Finish(f,3599).Outcome == QuestFinishOutcome.Rejected,
                "城镇提前33及未达成34均不能完成地下城演出");
            var receipt = EnterTarget();
            var original = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599);
            That(QuestHuntProgress.Bound(original,receipt.Entry) &&
                !QuestHuntProgress.Bound(original with { AcceptanceId = Guid.NewGuid() },receipt.Entry) &&
                !QuestHuntProgress.Bound(original with { Slot = checked((byte)(original.Slot+1)) },receipt.Entry),
                "演出绑定复用完整qid/slot/Acceptance三元组，不能只按任务号");
            var wrongMap = quest with { CinematicMapId = 76112 };
            var wrongDungeon = quest with { Definition = quest.Definition! with { Dungeons = [new(97,-1)] } };
            foreach (var wrong in new[] { wrongMap,wrongDungeon })
            {
                var catalog = new QuestCatalog(quests.All.Select(row => row.Id == quest.Id ? wrong : row).ToDictionary(row => row.Id));
                That(f.Session.ChangeQuest(3599,QuestLifecycleAction.Condition,catalog,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0))?.Commit is null,
                    "当前真实地图或地下城与演出声明不符时拒绝终末通知");
            }
            foreach (var malformed in new QuestClientCondition[] { new(0x10,0,0),new(null,1,0),new(null,0,1) })
                That(f.Session.ChangeQuest(3599,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,malformed)?.Commit is null,
                    "地下城演出只接受无channel/value0/reserved0的6B形状");

            That(Change(f,3599,QuestLifecycleAction.Giveup)?.Commit is not null &&
                Change(f,3599,QuestLifecycleAction.Accept)?.Commit is not null,
                "同一副本内放弃后重接产生新的接取身份");
            That(f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).AcceptanceId != original.AcceptanceId &&
                Change(f,3599,QuestLifecycleAction.Condition)?.Commit is null,
                "原Run绑定的旧接取不能替新接取签发进度，需重新入场绑定");
            f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            receipt = EnterTarget();
            var before = f.Read(); var snapshot = f.Session.DungeonSnapshot!;
            That(f.Session.ReadClearedQuest(3599) is null && f.Session.EnableFormalDungeonSettlement() is null,
                "演出终点仍没有独立清图与结算事实");
            f.Sql("CREATE TRIGGER dungeon_cinematic_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'cinematic rollback'); END");
            Reject<SqliteException>(() => Change(f,3599,QuestLifecycleAction.Condition),"地下城演出进度SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && QuestCompletionRepositoryTests.Same(before,f.Session.Selected!) &&
                f.Session.DungeonSnapshot!.Identity == snapshot.Identity,"SQL失败不安装角色进度或推进Run");
            f.Sql("DROP TRIGGER dungeon_cinematic_abort");
            var progress = Change(f,3599,QuestLifecycleAction.Condition)!;
            That(progress.Commit is not null && progress.Character.ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0 &&
                progress.Character.Learning.Experience == before.Learning.Experience &&
                f.Session.DungeonSnapshot!.Identity == snapshot.Identity &&
                f.Session.DungeonSnapshot!.LivingMonsters.SequenceEqual(snapshot.LivingMonsters) &&
                f.Session.ReadClearedQuest(3599) is null && f.Session.EnableFormalDungeonSettlement() is null,
                "原终末通知重试只保存scalar0，不生成死亡、经验、清图或结算入口");
            var progressIdentity = progress.DungeonIdentity!;
            That(f.Session.CanPublishQuestLifecycle(progress) && !f.Session.CanPublishQuestLifecycle(progress with
                { DungeonIdentity = progressIdentity with { Epoch = progressIdentity.Epoch-1 } }),
                "原Run回执可发布，切图前的旧epoch回执不能通过同一提交/发布门禁");
            var committed = f.Read();
            That(Change(f,3599,QuestLifecycleAction.Condition)?.Outcome == QuestLifecycleOutcome.Unchanged &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"重复33不增加revision或重复授予进度");
            f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            That(!f.Session.CanPublishQuestLifecycle(progress) &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0,
                "离场重选保留原接取持久进度，旧选角回执不能发布");
            var finished = Finish(f,3599);
            That(finished.Receipt?.Commit.ConditionType == 65 && f.Read().CompletedQuests.Contains(3599) &&
                Finish(f,3599).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "离场重登后沿普通34完成演出任务及真实奖励，重复34不重发");

            FormalDungeonReceipt EnterTarget()
            {
                var opening = f.Session.EnterFormalDungeon(96,3599,0,dungeons,rooms)
                    ?? throw new InvalidOperationException("3599真实演出实例未建立");
                That(Change(f,3599,QuestLifecycleAction.Condition) is null,"未完成地图加载不能提交演出33");
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                That(loaded.Snapshot.CurrentMap.Id != quest.CinematicMapId && Change(f,3599,QuestLifecycleAction.Condition)?.Commit is null,
                    "已加载的前序地图也不能提前满足终点演出");
                var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
                foreach (var point in path.Skip(1))
                {
                    foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.IsBoss))
                        That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                            "演出进度测试仅合成前序敌对演员死亡以到达真实终点");
                    That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,
                        "沿实际门墙进入演出地图");
                    That(Change(f,3599,QuestLifecycleAction.Condition) is null,"切图加载期拒绝终末通知");
                    loaded = f.Session.CompleteFormalDungeonLoading()!;
                }
                That(loaded.Snapshot.CurrentMap.Id == quest.CinematicMapId &&
                    loaded.Monsters.Monsters.Single(row => row.MonsterId == 62959) is { IsFriendly: true, CanReportDeath: false },
                    "真实76114的62959保留友方dummy，演出33不放行普通死亡");
                return loaded;
            }
        }
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

    // 3759 的范围条件已归零但自动归档被跳过时，父任务 3698 在米娅处先补归档再交付；未达成或离开半径仍然拒绝。
    public static void RunSmanyRangeRecovery(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var parent = quests.Get(3698); var meet = quests.Get(3758); var range = quests.Get(3759);
        var slough = towns.Areas.Values.Single(area => area.Phases.Prepend(area.Default).Any(map =>
            map.Path.Contains("Slough_up_street.map",StringComparison.OrdinalIgnoreCase) && map.Npcs.Any(npc => npc.Id == 78 && npc.X == 1397 && npc.Y == 210)));
        var gent = towns.Areas.Values.Single(area => area.Phases.Prepend(area.Default).Any(map =>
            map.Npcs.Any(npc => npc.Id == 59 && npc.X == 2004 && npc.Y == 187)));
        That(parent.Path == "n_quest/quest_renewal/epic_quest/slough/slough_epic_03.qst" &&
            parent.Condition == "[quest clear]" && parent.SubType == 2 && parent.IntData.SequenceEqual([3758,3759]) && parent.CompleteNpc == 78 &&
            meet.Path == "n_quest/quest_renewal/epic_quest/slough/slough_epic_03_01.qst" &&
            meet.Condition == "[meet npc]" && meet.IntData.SequenceEqual([59]) && meet.MainQuest == 3698 &&
            QuestRelationProgress.ZeroRewardChild(meet) && QuestRelationProgress.TownLinkedChild(meet) &&
            range.Path == "n_quest/quest_renewal/epic_quest/slough/slough_epic_03_02.qst" &&
            range.Condition == "[reach the range]" && range.SubType == 0 && range.IntData.SequenceEqual([78,50,50]) &&
            range.MainQuest == 3698 && range.CompleteNpc == 78 && QuestRelationProgress.ZeroRewardChild(range) &&
            !QuestRelationProgress.TownLinkedChild(range) && QuestRelationProgress.SequentialParent(parent,quests),
            "3698/3758/3759保持斯曼工业基地顺序父任务、梅尔文会面和米娅范围零奖励");
        That(slough.Shifts.Select(shift => (shift.Quest,shift.Index)).SequenceEqual([(3779,0),(4869,1)]) &&
            slough.Phases.Prepend(slough.Default).All(map => map.Npcs.Any(npc => npc.Id == 78 && npc.X == 1397 && npc.Y == 210)) &&
            gent.Phases.Prepend(gent.Default).All(map => map.Npcs.Any(npc => npc.Id == 59 && npc.X == 2004 && npc.Y == 187)),
            "米娅78在斯曼三个阶段地图坐标不变，梅尔文59在根特车间各阶段都在");
        var blocked = parent.Definition!.Collisions.Concat(meet.Definition!.Collisions).Concat(range.Definition!.Collisions).ToHashSet();
        var done = parent.Definition.PrerequisiteGroups.First(group => group.Length > 0).ToHashSet();
        That(done.Contains(3697),"3698前置组包含3697");
        Location? melvin = null, near = null, far = null;
        for (int attempt = 0; attempt < 2 && (melvin is null || near is null || far is null); attempt++)
        {
            melvin = Walk(towns,gent.Town,gent.Area,done,_ => true);
            near = Walk(towns,slough.Town,slough.Area,done,point => Math.Abs(point.X - 1397) <= 90 && Math.Abs(point.Y - 210) <= 90);
            far = Walk(towns,slough.Town,slough.Area,done,point => Math.Abs((long)point.X - 1397) > 90 || Math.Abs((long)point.Y - 210) > 90);
            if (attempt == 0 && (melvin is null || near is null || far is null))
                foreach (int id in slough.Quests.Concat(slough.AnyQuests.Take(1)).Concat(gent.Quests).Concat(gent.AnyQuests.Take(1))
                    .Concat(slough.Phases.Prepend(slough.Default).Concat(gent.Phases.Prepend(gent.Default))
                        .SelectMany(map => map.Bounds.SelectMany(region => region.Quests))))
                    if (id > 0 && !blocked.Contains(id)) done.Add(id);
        }
        That(melvin is not null && near is not null && far is not null,
            $"梅尔文与米娅半径内外都有可行走点：melvin={melvin} near={near} far={far}");
        foreach (int[] extra in new[] { Array.Empty<int>(), new[] { 3779 }, new[] { 3779,4869 } })
        {
            var phaseDone = done.Concat(extra).ToHashSet();
            var phase = slough.Map(phaseDone);
            var phaseNear = Walk(towns,slough.Town,slough.Area,phaseDone,point => Math.Abs(point.X - 1397) <= 90 && Math.Abs(point.Y - 210) <= 90);
            var phaseFar = Walk(towns,slough.Town,slough.Area,phaseDone,point => Math.Abs((long)point.X - 1397) > 90 || Math.Abs((long)point.Y - 210) > 90);
            That(phase.Npcs.Any(npc => npc.Id == 78 && npc.X == 1397 && npc.Y == 210) && phaseNear is not null && phaseFar is not null &&
                QuestClientSceneProgress.Matches(Sample(phaseNear,phaseDone),range,towns) &&
                !QuestClientSceneProgress.Matches(Sample(phaseFar,phaseDone),range,towns),
                "阶段映射后米娅半径判定仍用当前地图坐标：" + phase.Path);
        }
        using (var f = Open())
        {
            AcceptChain(f);
            var unmet = f.Read();
            That(Finish(f,3698).Outcome == QuestFinishOutcome.Rejected &&
                f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,3698) is null &&
                QuestCompletionRepositoryTests.Same(unmet,f.Read()) && Receipts(f,3759) == 0 && Receipts(f,3698) == 0,
                "3759仍为1时，站在米娅处也不能补归档或交付父任务");
            Place(f,far!);
            That(Condition(f,3759)?.Commit is null && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3759).ConditionState == 1,
                "离开米娅半径的范围33不把3759写成0");
            Place(f,near!);
            var zeroed = Condition(f,3759);
            That(zeroed?.Commit is not null && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3759).ConditionState == 0 &&
                !f.Read().CompletedQuests.Contains(3759) && QuestClientSceneProgress.SatisfiedTownRange(f.Read(),range,towns),
                "范围内33只保存3759达成，不在会话入口自动归档");
            Place(f,far!);
            var away = f.Read();
            That(!QuestClientSceneProgress.SatisfiedTownRange(away,range,towns) &&
                Finish(f,3698).Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(away,f.Read()),
                "条件已归零但人已离开半径时，父任务交付不放宽坐标、不补归档");
            Place(f,near!);
            ulong beforeExp = f.Read().Learning.Experience;
            var finished = Finish(f,3698);
            var after = f.Read();
            That(finished.Outcome == QuestFinishOutcome.Committed && finished.Receipt?.Commit.ExperienceReward.Amount is uint gained &&
                after.Learning.Experience == beforeExp + gained && after.CompletedQuests.Contains(3759) && after.CompletedQuests.Contains(3698) &&
                after.ActiveMainQuests.All(row => row.QuestId != 3759 && row.QuestId != 3698) && Receipts(f,3759) == 1 && Receipts(f,3698) == 1,
                "直接交付3698先补归档零奖励3759，父奖励只记一次：" + finished.Outcome + " / " + finished.Reason);
            That(Finish(f,3698).Outcome == QuestFinishOutcome.AlreadyCompleted && Finish(f,3759).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(after,f.Read()) && Receipts(f,3759) == 1 && Receipts(f,3698) == 1,
                "重复交付3698和3759不再发奖或写第二笔回执");
        }
        using (var f = Open())
        {
            AcceptChain(f);
            Place(f,near!);
            That(Condition(f,3759)?.Commit is not null,"父回调路径再次把3759条件归零且不归档");
            ulong beforeExp = f.Read().Learning.Experience;
            var closed = f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,3698);
            var archived = f.Read();
            That(closed is { ArchivedTownRange: true, Commit: { ExperienceReward.Amount: 0, Rewards.Length: 0, GoldAmount: 0 } } &&
                archived.Learning.Experience == beforeExp && archived.CompletedQuests.Contains(3759) && !archived.CompletedQuests.Contains(3698) &&
                archived.ActiveMainQuests.Single(row => row.QuestId == 3698).ConditionState == 0 && Receipts(f,3759) == 1 && Receipts(f,3698) == 0,
                "父33回调补归档已达成的3759，父scalar归零，不发父奖励");
            var finished = Finish(f,3698);
            var rewarded = f.Read();
            That(finished.Outcome == QuestFinishOutcome.Committed && rewarded.CompletedQuests.Contains(3698) &&
                rewarded.Learning.Experience == beforeExp + finished.Receipt!.Commit.ExperienceReward.Amount &&
                Receipts(f,3698) == 1 && Receipts(f,3759) == 1,
                "回调后再交付父任务只发一次奖励：" + finished.Reason);
            That(f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,3698) is null &&
                Finish(f,3698).Outcome == QuestFinishOutcome.AlreadyCompleted && QuestCompletionRepositoryTests.Same(rewarded,f.Read()),
                "重复父回调和父34保持幂等");
        }
        using (var f = Open())
        {
            AcceptChain(f);
            Place(f,near!);
            That(Condition(f,3759)?.Commit is not null,"投影失效场景先把3759条件归零");
            var selected = f.Session.Selected!;
            var preparation = QuestCompletionPlan.Prepare(selected,range,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
            That(preparation.Outcome == QuestCompletionOutcome.Accepted && preparation.Plan is not null,"3759当前投影可以按原规则归档");
            f.Repository.CommitQuestCompletion(f.Owner,preparation.Plan!);
            f.Sql("UPDATE characters SET revision=revision+1");
            var pendingType = typeof(GameSession).GetNestedType("PendingQuestCompletion",BindingFlags.NonPublic)!;
            var pending = Activator.CreateInstance(pendingType,BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,null,
                [preparation.Plan,f.Session.SelectionEpoch,f.Session.Phase,null],null);
            That(pending is not null,"可以冻结3759原领奖计划");
            typeof(GameSession).GetField("pendingQuestCompletion",BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Session,pending);
            var aborted = Finish(f,3698);
            var persisted = f.Read();
            That(aborted.Outcome == QuestFinishOutcome.Committed && aborted.Receipt is { Commit.QuestId: 3759, Commit.ProjectionCurrent: false } &&
                !f.Session.CanPublishQuestCompletion(aborted.Receipt) && ReferenceEquals(selected,f.Session.Selected) &&
                !persisted.CompletedQuests.Contains(3698) && persisted.CompletedQuests.Contains(3759) &&
                Receipts(f,3759) == 1 && Receipts(f,3698) == 0,
                "子任务提交后投影失效时终止父任务，不安装角色、不发布、也不交付3698");
        }
        var rewardedParent = parent with { Rewards = [new(0,2058,null), new(0,1,null)] };
        var rewardCatalog = new QuestCatalog(quests.All.Select(row => row.Id == 3698 ? rewardedParent : row).ToDictionary(row => row.Id));
        using (var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,rewardCatalog,false,81,done.ToArray(),growType:5))
        {
            Place(f,melvin!);
            That(f.Session.ChangeQuest(3698,QuestLifecycleAction.Accept,rewardCatalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null &&
                f.Session.ChangeQuest(3758,QuestLifecycleAction.Accept,rewardCatalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null &&
                f.Session.ChangeQuest(3758,QuestLifecycleAction.Condition,rewardCatalog,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0))?.Commit is not null &&
                f.Session.FinishQuest(3758,ushort.MaxValue,rewardCatalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Committed &&
                f.Session.ChangeQuest(3759,QuestLifecycleAction.Accept,rewardCatalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "满包场景沿原链接取3698/3758/3759");
            Place(f,near!);
            That(f.Session.ChangeQuest(3759,QuestLifecycleAction.Condition,rewardCatalog,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0))?.Commit is not null,
                "满包场景范围内把3759归零且不归档");
            int granted = 0;
            ushort grantError = 0;
            int chunk = items.Items.Get(2058).StackLimit;
            while (granted < 256)
            {
                var grant = f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,[new(2058,chunk)],
                    items.Items,items.GoldLimits,items.EpicPieces));
                if (!grant.Accepted) { grantError = grant.Error; break; }
                granted++;
            }
            That(granted > 0 && grantError == 17,"背包被2058填到错误17：" + granted + "/" + grantError);
            var rejected = f.Session.FinishQuest(3698,ushort.MaxValue,rewardCatalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(rejected.Outcome == QuestFinishOutcome.InsufficientSpace && rejected.Failure == QuestCompletionFailure.InventoryCapacity &&
                rejected.ArchivedChildren.Length == 0 && !f.Read().CompletedQuests.Contains(3759) && !f.Read().CompletedQuests.Contains(3698) &&
                Receipts(f,3759) == 0 && Receipts(f,3698) == 0,
                "父任务奖励放不下时整笔回滚，3759不归档：" + rejected.Outcome + " / " + rejected.Reason);
            That(f.Session.MutateInventory(character =>
            {
                var item = character.Inventory.Items.First(row => row.Core.ItemId == 2058 && row.Position.List == InventoryListType.Main);
                return InventoryDeleteService.Plan(character.Inventory,item.Position,item.Core.Value,item.Core.IsStackable ? 1 : item.Core.Count);
            }).Accepted,"腾出一个2058名额后再交付父任务");
            var finished = f.Session.FinishQuest(3698,ushort.MaxValue,rewardCatalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            var after = f.Read();
            That(finished.Outcome == QuestFinishOutcome.Committed && after.CompletedQuests.Contains(3759) && after.CompletedQuests.Contains(3698) &&
                Receipts(f,3759) == 1 && Receipts(f,3698) == 1 &&
                f.Session.FinishQuest(3698,ushort.MaxValue,rewardCatalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                Receipts(f,3759) == 1 && Receipts(f,3698) == 1,
                "腾出空间后补归档只发生一次，父奖励也只发一次：" + finished.Reason);
        }
        ParentCommitFaults().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} 斯曼工业基地3698范围子任务补归档与父提交重试断言（非实机）");

        QuestSceneTests.Fixture Open() => new(towns,characters,skills,growth,quests,false,81,done.ToArray(),growType:5);
        void AcceptChain(QuestSceneTests.Fixture f)
        {
            Place(f,melvin!);
            That(Accept(f,3698)?.Commit is not null && Accept(f,3758)?.Commit is not null,"剑影接取3698和会面子任务3758");
            That(Condition(f,3758)?.Commit is not null && Finish(f,3758).Outcome == QuestFinishOutcome.Committed &&
                f.Read().CompletedQuests.Contains(3758) && !f.Read().CompletedQuests.Contains(3698),
                "梅尔文所在地图完成3758，父任务仍待米娅");
            That(Accept(f,3759)?.Commit is not null && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3759).ConditionState == 1,
                "3758归档后接取范围子任务3759，初值仍是1");
        }
        void Place(QuestSceneTests.Fixture f,Location point)
        {
            var current = f.Read();
            f.Repository.Save(f.Owner,current,point,current.Return);
            f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            That(f.Read().Position.Town == point.Town && f.Read().Position.Area == point.Area &&
                f.Read().Position.X == point.X && f.Read().Position.Y == point.Y,"测试落点保持在指定城镇坐标");
        }
        QuestLifecycleReceipt? Accept(QuestSceneTests.Fixture f,ushort id) => f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,
            items.Items,items.GoldLimits,items.EpicPieces);
        QuestLifecycleReceipt? Condition(QuestSceneTests.Fixture f,ushort id) => f.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,
            items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
        QuestFinishResult Finish(QuestSceneTests.Fixture f,ushort id) => f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,
            items.Items,items.GoldLimits,items.EpicPieces,growth);
        static long Receipts(QuestSceneTests.Fixture f,int questId) =>
            Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=" + questId));
        static Character Sample(Location point,IReadOnlySet<int> completed) => new(1,0,"Range",1,point,point) { Level = 81, CompletedQuests = completed.ToArray() };
        static Location? Walk(TownCatalog towns,byte town,byte area,IReadOnlySet<int> done,Func<Location,bool> match)
        {
            var map = towns.Areas[(town,area)].Map(done);
            foreach (var region in map.Bounds)
            {
                var box = region.Bounds;
                for (int y = box.Y; y <= box.Y + box.Height; y += 15)
                for (int x = box.X; x <= box.X + box.Width; x += 15)
                {
                    if (x is < 0 or > ushort.MaxValue || y is < 0 or > ushort.MaxValue) continue;
                    var point = new Location(town,area,(ushort)x,(ushort)y);
                    if (towns.IsPosition(point,done) && match(point)) return point;
                }
            }
            return null;
        }

        // 子任务已经落库之后，父提交前失败或返回丢失都把补归档前的活动表留在冻结计划上。
        // 同一计划再走真实完成处理器：574 先于 356，奖励和回执只发生一次。
        async Task ParentCommitFaults()
        {
            That(!quests.TryGet(65534,out _),"65534不是已装载任务，另一次34不会误归档");
            await Fault(afterCommit:false,recover:true);
            await Fault(afterCommit:true,recover:true);
            await Fault(afterCommit:false,recover:false);
        }

        async Task Fault(bool afterCommit,bool recover)
        {
            using var f = Open();
            AcceptChain(f);
            Place(f,near!);
            That(Condition(f,3759)?.Commit is not null,Label(afterCommit,recover) + "先把3759归零且不归档");
            var before = f.Session.Selected!;
            ulong beforeExp = before.Learning.Experience;
            var proxy = QuestCommitFaultProxy.Arm(f.Session,3698,afterCommit,busy:false);
            using var log = new ServerLog(Path.GetTempPath(),fileEnabled:false);
            var projection = new QuestProjection(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            var registry = new GameCommandRegistry();
            new QuestCompletionHandler(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,log,
                new CharacterLevelProjection(new CharacterStatComputer(characters),projection),projection).Register(registry);
            registry.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            await using var sender = new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,_,_) => { });
            var context = new GameConnectionContext(new(88,IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
            var packet = new Packet(1,34,FinishBody(3698));
            Exception? fault = null;
            try { await registry.DispatchAsync(context,packet); }
            catch (IOException error) { fault = error; }
            That(fault is IOException && stream.Length == 0 && proxy.ParentCalls == 1 && proxy.FirstDuplicate == (afterCommit ? false : null) && !proxy.SamePlan,
                Label(afterCommit,recover) + "父提交故障不发帧 fault=" + (fault?.GetType().Name ?? "none") +
                " calls=" + proxy.ParentCalls + " first=" + (proxy.FirstDuplicate?.ToString() ?? "null") +
                " same=" + proxy.SamePlan + " bytes=" + stream.Length);
            That(Receipts(f,3759) == 1 && f.Session.Selected!.ActiveMainQuests.Any(row => row.QuestId == 3698) &&
                f.Session.Selected.ActiveMainQuests.All(row => row.QuestId != 3759) && !ReferenceEquals(before,f.Session.Selected),
                Label(afterCommit,recover) + "子任务已归档，会话仍停在归档后的父槽快照");
            var remembered = Remembered(f.Session);
            That(remembered.Any(row => row.QuestId == 3759) && remembered.Any(row => row.QuestId == 3698),
                Label(afterCommit,recover) + "冻结计划留下补归档前的活动表");
            var parked = f.Session.Selected!;
            if (!afterCommit)
                That(Receipts(f,3698) == 0 && f.Read().Learning.Experience == beforeExp && !f.Read().CompletedQuests.Contains(3698),
                    "父提交前失败没有父回执、经验和完成位");
            else
            {
                ulong persisted = f.Read().Learning.Experience;
                That(Receipts(f,3698) == 1 && persisted > beforeExp && f.Read().CompletedQuests.Contains(3698) &&
                    !parked.CompletedQuests.Contains(3698) && parked.Learning.Experience == beforeExp,
                    "父提交已落库但返回丢失，会话还没装上父回执");
            }
            if (!recover)
            {
                f.Sql("UPDATE characters SET revision=revision+1");
                Exception? stale = null;
                try { await registry.DispatchAsync(context,packet); }
                catch (InvalidOperationException error) { stale = error; }
                That(stale is InvalidOperationException && stale.Message.Contains("revision",StringComparison.Ordinal) &&
                    stream.Length == 0 && proxy.ParentCalls == 2 && proxy.SamePlan && Receipts(f,3698) == 0 &&
                    ReferenceEquals(parked,f.Session.Selected),
                    "revision已变且尚无父回执时精确重试抛出，不发帧也不安装新角色");
                return;
            }
            var closed = await registry.DispatchAsync(context,new Packet(1,34,FinishBody(65534)));
            That(closed == CommandResult.CloseConnection && stream.Length == 0 && ReferenceEquals(parked,f.Session.Selected) &&
                Receipts(f,3759) == 1 && Receipts(f,3698) == (afterCommit ? 1 : 0),
                Label(afterCommit,recover) + "另一次请求拿不走活动表，关闭连接且不发过期槽");
            ulong persistedExp = f.Read().Learning.Experience;
            var result = await registry.DispatchAsync(context,packet);
            var frames = ReadFrames(stream);
            int ack = frames.FindIndex(row => row.Kind == 1 && row.Opcode == 34);
            int activeAt = frames.FindLastIndex(row => row.Kind == 0 && row.Opcode == 574);
            int completedAt = frames.FindLastIndex(row => row.Kind == 0 && row.Opcode == 356);
            var loaded = f.Read();
            byte[] active = QuestPackets.ActiveList(loaded.ActiveMainQuests);
            That(result == CommandResult.Handled && ack >= 0 && frames[ack].Body[0] == 1 && activeAt > ack && completedAt > activeAt &&
                frames.Where(row => row.Opcode == 574).All(row => row.Body.AsSpan().SequenceEqual(active)),
                Label(afterCommit,recover) + "同一计划重试先发574再发356，574是提交后的活动表");
            Bytes(frames[completedAt].Body,TownPackets.CompletedQuests(loaded),Label(afterCommit,recover) + "的356与完成表一致");
            That(!active.AsSpan().SequenceEqual(QuestPackets.ActiveList(remembered)),
                Label(afterCommit,recover) + "的574对照表仍含已删掉的范围子任务");
            That(loaded.CompletedQuests.Contains(3759) && loaded.CompletedQuests.Contains(3698) &&
                Receipts(f,3759) == 1 && Receipts(f,3698) == 1 && loaded.Learning.Experience == persistedExp + (afterCommit ? 0UL : BinaryPrimitives.ReadUInt32LittleEndian(frames[ack].Body.AsSpan(4))) &&
                proxy.ParentCalls == 2 && proxy.SamePlan && proxy.FirstDuplicate == false && proxy.LastDuplicate == afterCommit,
                Label(afterCommit,recover) + "奖励和父子回执只发生一次，重试沿用原计划");
            stream.SetLength(0);
            var repeat = await registry.DispatchAsync(context,packet);
            var rejected = ReadFrames(stream);
            That(repeat == CommandResult.Handled && rejected.Count == 1 && rejected[0].Kind == 1 && rejected[0].Opcode == 34,
                Label(afterCommit,recover) + "重复34只回一帧");
            Bytes(rejected[0].Body,QuestPackets.Rejected(19),Label(afterCommit,recover) + "重复34拒绝");
            That(Receipts(f,3698) == 1 && Receipts(f,3759) == 1 && f.Read().Learning.Experience == loaded.Learning.Experience,
                Label(afterCommit,recover) + "重复34不再发奖");
            f.Session.ReturnToRoster();
            f.Session.Select(0,resumeTown:true);
            f.Session.MarkReady();
            var restored = f.Session.Selected!;
            That(QuestCompletionRepositoryTests.Same(f.Read(),restored) &&
                restored.CompletedQuests.Contains(3759) && restored.CompletedQuests.Contains(3698) &&
                restored.ActiveMainQuests.All(row => row.QuestId != 3759 && row.QuestId != 3698),
                Label(afterCommit,recover) + "重选后与持久活动槽和完成表一致");
        }

        static string Label(bool afterCommit,bool recover) => (afterCommit ? "返回丢失" : "提交前失败") + (recover ? "重试：" : "失效：");

        static IReadOnlyList<ActiveMainQuest> Remembered(GameSession session)
        {
            var pending = typeof(GameSession).GetField("pendingQuestCompletion",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
            That(pending is not null,"父提交故障后冻结计划仍在");
            return (IReadOnlyList<ActiveMainQuest>)pending!.GetType().GetProperty("RangePreviousQuests")!.GetValue(pending)!;
        }

        static List<(byte Kind,ushort Opcode,byte[] Body)> ReadFrames(MemoryStream stream)
        {
            var bytes = stream.ToArray();
            var frames = new List<(byte Kind,ushort Opcode,byte[] Body)>();
            int at = 0;
            while (at + 16 <= bytes.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 3));
                That(length >= 16 && at + length <= bytes.Length,"领奖帧长度完整");
                frames.Add((bytes[at],BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 1)),bytes.AsSpan(at + 16,length - 16).ToArray()));
                at += length;
            }
            That(at == bytes.Length,"领奖帧没有剩余字节");
            return frames;
        }

        static byte[] FinishBody(ushort id)
        {
            var body = new byte[10];
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0),34);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),id);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6),1);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8),ushort.MaxValue);
            return body;
        }
    }
}
