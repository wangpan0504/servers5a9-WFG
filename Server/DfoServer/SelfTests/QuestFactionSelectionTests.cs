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

                void Change(QuestLifecycleAction action, QuestClientCondition? condition = null)
                {
                    var receipt = f.Session.ChangeQuest(id, action, quests, items.Items, items.GoldLimits, items.EpicPieces, condition);
                    That(receipt?.Commit is not null, $"原阵营任务{action}：{receipt?.Reason}");
                }
                QuestFinishResult Finish() => f.Session.FinishQuest(id, ushort.MaxValue, quests, parameters,
                    items.Items, items.GoldLimits, items.EpicPieces, growth);
            }
        }
        Console.WriteLine($"PASS {Count - checks} 原场景阵营问题：Lv87/95、男女鬼剑全部选项、重选与重复交付");
    }
}
