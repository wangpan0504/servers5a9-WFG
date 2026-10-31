using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestFactionSelectionTests
{
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning)
    {
        int checks = Count;
        var quests = SelfTestCatalogs.Quests(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var area = towns.Areas[(22, 3)];
        foreach (var (job, id) in new (byte Job, ushort Id)[] { (0, 3868), (11, 3869) })
        {
            var question = quests.Get(id);
            That(QuestQuestionProgress.TryCount(question, out int options), "原阵营任务具有完整问题选项");
            foreach (byte level in new byte[] { 87, 95 })
            for (byte answer = 0; answer < options; answer++)
            {
                var done = question.Definition!.PrerequisiteGroups.Single().Concat(area.Quests).ToHashSet();
                var map = area.Map(done);
                That(map.Npcs.Any(row => row.Id == 331), "原引导22/3阶段具有NPC331");
                var bounds = map.Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                var position = new Location(22, 3, checked((ushort)(bounds.X + bounds.Width / 2)),
                    checked((ushort)(bounds.Y + bounds.Height / 2)));
                using var f = new QuestSceneTests.Fixture(towns, characters, learning, growth, quests, false,
                    level, done.ToArray(), job: job, position: position);
                Change(QuestLifecycleAction.Accept);
                var unanswered = f.Read();
                That(unanswered.ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == QuestQuestionProgress.Unanswered,
                    "原等级场景接取后没有默认选择阵营");
                That(Finish().Outcome == QuestFinishOutcome.Rejected, "未选择阵营不能提前交付");
                var before = f.Read();
                foreach (byte[] invalid in new byte[][]
                {
                    [33, 0, (byte)id, (byte)(id >> 8), 0, checked((byte)options)],
                    [33, 0, (byte)id, (byte)(id >> 8), 1, 0]
                })
                {
                    That(QuestRequests.TryTrigger(invalid, out var rejected), "原生回答拒绝样本结构合法");
                    That(f.Session.ChangeQuest(id, QuestLifecycleAction.Condition, quests, items.Items, items.GoldLimits,
                        items.EpicPieces, new(rejected.Channel, rejected.Value, rejected.Reserved))?.Commit is null &&
                        QuestCompletionRepositoryTests.Same(before, f.Read()), "越界答案或非零类型不改变活动问题及资产");
                }
                // 0202B480先写类型0，再写零基答案；必须经过真实Parser保留该顺序。
                That(QuestRequests.TryTrigger([33, 0, (byte)id, (byte)(id >> 8), 0, answer], out var request),
                    "原生问题请求包含类型0和完整答案");
                Change(QuestLifecycleAction.Condition, new(request.Channel, request.Value, request.Reserved));
                f.Session.ReturnToRoster();
                f.Session.Select(0, resumeTown: true);
                f.Session.MarkReady();
                That(f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == answer,
                    "重选恢复本次零基答案，不恢复为未回答或默认第二项");
                var result = Finish();
                That(result.Outcome == QuestFinishOutcome.Committed,
                    $"原场景阵营交付{id}/job{job}/level{level}/answer{answer}：{result.Reason}");
                var completed = f.Read();
                That(completed.QuestAnswers[id] == answer && !completed.ActiveMainQuests.Any(row => row.QuestId == id),
                    "原阵营问题事务保存答案并释放活动槽");
                var branches = quests.All.Where(row => row.Definition?.RequiredAnswers.Any(required => required.QuestId == id) == true).ToArray();
                That(branches.Count(row => QuestEligibility.Evaluate(completed, row, quests).Eligible) == 1 &&
                    branches.Single(row => QuestEligibility.Evaluate(completed, row, quests).Eligible)
                        .Definition!.RequiredAnswers.Single(required => required.QuestId == id).Answer == answer,
                    "全部真实选项都只开放自身后继，不开放其它阵营");
                That(Finish().Outcome == QuestFinishOutcome.AlreadyCompleted &&
                    QuestCompletionRepositoryTests.Same(completed, f.Read()), "重复交付不重复奖励或改变答案");

                var branch = branches.Single(row => QuestEligibility.Evaluate(completed, row, quests).Eligible);
                var accepted = f.Session.ChangeQuest(checked((ushort)branch.Id), QuestLifecycleAction.Accept, quests,
                    items.Items, items.GoldLimits, items.EpicPieces);
                That(accepted?.Commit is not null, $"阵营分支{branch.Id}可真实接取：{accepted?.Reason}");
                var branchState = f.Read();
                var branchDone = QuestEligibility.Completed(branchState);
                var meetingArea = towns.Areas.Values.FirstOrDefault(row => row.Town == 22 &&
                    row.Map(branchDone).Npcs.Any(npc => npc.Id == branch.IntData[0]));
                That(meetingArea is not null, $"阵营分支{branch.Id}当前阶段存在目标NPC{branch.IntData[0]}");
                var destination = towns.Spawn(new(meetingArea!.Town, meetingArea.Area, 0, 0), branchDone);
                That(destination is not null, "阵营对话地图具有合法落点");
                f.Session.ReturnToRoster();
                f.Repository.Save(f.Owner, f.Read(), destination!, destination);
                f.Session.Select(0, resumeTown: true);
                f.Session.MarkReady();
                var meeting = f.Session.ChangeQuest(checked((ushort)branch.Id), QuestLifecycleAction.Condition, quests,
                    items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
                That(meeting?.Commit is not null, $"阵营分支{branch.Id}可在真实NPC场景对话：{meeting?.Reason}");
                var joined = f.Session.FinishQuest(checked((ushort)branch.Id), ushort.MaxValue, quests, parameters,
                    items.Items, items.GoldLimits, items.EpicPieces, growth);
                That(joined.Outcome == QuestFinishOutcome.Committed, $"阵营分支{branch.Id}可交付：{joined.Reason}");
                var merged = f.Read();
                That(QuestEligibility.Evaluate(merged, quests.Get(3875), quests).Eligible,
                    $"阵营分支{branch.Id}真实交付后开放共同主线3875");
                That(branches.Where(row => row.Id != branch.Id).All(row => !merged.CompletedQuests.Contains(row.Id)),
                    "阵营合流不伪造其它分支完成记录");
                var common = f.Session.ChangeQuest(3875, QuestLifecycleAction.Accept, quests,
                    items.Items, items.GoldLimits, items.EpicPieces);
                That(common?.Commit is not null, $"阵营分支{branch.Id}可接取合流主线3875：{common?.Reason}");
                var gateArea = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                    towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 195));
                var gate = gateArea.Map(QuestEligibility.Completed(f.Read())).DungeonGates.First().Bounds;
                var entrance = new Location(gateArea.Town, gateArea.Area,
                    checked((ushort)(gate.X + gate.Width / 2)), checked((ushort)(gate.Y + gate.Height / 2)));
                f.Session.ReturnToRoster();
                f.Repository.Save(f.Owner, f.Read(), entrance, entrance);
                f.Session.Select(0, resumeTown: true);
                f.Session.MarkReady();
                var opening = f.Session.EnterFormalDungeon(195, 3875, 0, SelfTestCatalogs.Dungeons(pvf), SelfTestCatalogs.Rooms(pvf));
                That(opening is { Entry.RouteQuestId: 3875, Entry.BossMap.Id: 70539, Entry.HasExplicitClearMap: true },
                    $"阵营分支{branch.Id}合流后按真实主线进入王的书库目标图70539：{f.Session.LastFormalDungeonRejection}; route={opening?.Entry.RouteQuestId}, clear={opening?.Entry.QuestClearMap?.Id}");

                void Change(QuestLifecycleAction action, QuestClientCondition? condition = null)
                {
                    var receipt = f.Session.ChangeQuest(id, action, quests, items.Items, items.GoldLimits, items.EpicPieces, condition);
                    That(receipt?.Commit is not null, $"原阵营任务{action}：{receipt?.Reason}");
                }
                QuestFinishResult Finish() => f.Session.FinishQuest(id, ushort.MaxValue, quests, parameters,
                    items.Items, items.GoldLimits, items.EpicPieces, growth);
            }
        }
        Console.WriteLine($"PASS {Count - checks} 原场景阵营问题：Lv87/95、男女鬼剑全部选项、重选、真实分支交付及主线合流入图");
    }
}
