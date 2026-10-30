using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestHellChainTests
{
    internal static void Run(TcpTestResources resources)
    {
        int checks = Count;
        var quests = resources.Quests;
        var items = resources.ItemResources;
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(resources.Archive);
        foreach (var (finalId, hellId, introductionId) in InfernoQuestUnlock.Pairs)
        {
            var hell = quests.Get(hellId);
            var introduction = quests.Get(introductionId);
            var final = quests.Get(finalId);
            byte level = checked((byte)Math.Max(introduction.MinimumLevel, hellId == 4539 ? 82 : 0));
            var done = hell.Prerequisites.ToHashSet();
            var area = resources.Towns.Areas.Values.First(row => row.Level <= level &&
                row.Map(done).Npcs.Any(npc => npc.Id == introduction.IntData[0]));
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length > 0 && !area.AnyQuests.Any(done.Contains)) done.Add(area.AnyQuests[0]);
            That(!done.Contains(hellId) && !done.Contains(introductionId) && !done.Contains(finalId),
                "真实深渊三步链不预填资格、首步或尾步完成记录");
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                checked((ushort)(bounds.Y + bounds.Height / 2)));
            using var f = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, quests, false, level, done.ToArray(), position: position);
            Accept(hell);
            Finish(hell);
            That(QuestEligibility.Evaluate(f.Read(), introduction, quests).Eligible &&
                !QuestEligibility.Evaluate(f.Read(), final, quests).Eligible,
                $"区域深渊{hellId}实际交付后只开放外传首步{introductionId}");
            Accept(introduction);
            That(TownPackets.CompletedQuests(f.Read())[4 + finalId] == 0,
                "外传首次接取立即撤销尾步的深渊兼容显示位");
            if (introduction.SubType == -1)
            {
                var condition = f.Session.ChangeQuest(checked((ushort)introductionId), QuestLifecycleAction.Condition,
                    quests, items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
                That(condition?.Commit is not null, $"原NPC完成外传首步{introductionId}：{condition?.Reason}");
            }
            else That(introduction.SubType == 1 && f.Read().ActiveMainQuests.Single(row => row.QuestId == introductionId).ConditionState == 0,
                "外传自动达成首步保持其资源语义，不强制发送会面33");
            Finish(introduction);
            That(QuestEligibility.Evaluate(f.Read(), final, quests).Eligible &&
                TownPackets.CompletedQuests(f.Read())[4 + finalId] == 0,
                $"外传首步{introductionId}交付后尾步{finalId}立即可接且不是已完成");
            Accept(final);
            f.Session.ReturnToRoster();
            f.Session.Select(0, resumeTown: true);
            f.Session.MarkReady();
            That(f.Read().ActiveMainQuests.Any(row => row.QuestId == finalId) &&
                TownPackets.CompletedQuests(f.Read())[4 + finalId] == 0, "重选不把已接尾步覆盖为兼容完成位");
            Finish(final);
            var completed = f.Read();
            That(new[] { hellId, introductionId, finalId }.All(completed.CompletedQuests.Contains) &&
                TownPackets.CompletedQuests(completed)[4 + finalId] == 1,
                "三步均由各自事务建立真实完成记录");
            That(FinishAgain().Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(completed, f.Read()), "尾步重复交付不重复发奖");

            void Accept(StoryQuest quest)
            {
                var receipt = f.Session.ChangeQuest(checked((ushort)quest.Id), QuestLifecycleAction.Accept,
                    quests, items.Items, items.GoldLimits, items.EpicPieces);
                That(receipt?.Commit is not null, $"真实深渊链接取{quest.Id}：{receipt?.Reason}");
            }
            void Finish(StoryQuest quest)
            {
                var result = f.Session.FinishQuest(checked((ushort)quest.Id), ushort.MaxValue, quests, parameters,
                    items.Items, items.GoldLimits, items.EpicPieces, growth);
                That(result.Outcome == QuestFinishOutcome.Committed, $"真实深渊链交付{quest.Id}：{result.Reason}");
            }
            QuestFinishResult FinishAgain() => f.Session.FinishQuest(checked((ushort)finalId), ushort.MaxValue,
                quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
        }
        Console.WriteLine($"PASS {Count - checks} 五条深渊资格与外传真实三步衔接、显示位、重选及重复交付");
    }
}
