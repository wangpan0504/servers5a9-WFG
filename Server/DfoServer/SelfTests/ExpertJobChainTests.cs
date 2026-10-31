using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobChainTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning,
        CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters, ItemResources resources, QuestCatalog quests)
    {
        int checks = Count;
        var scripts = resources.ExpertJobs;
        var area = towns.Areas.Values.First(area => area.Default.Npcs.Any(npc => npc.Id == 144));
        var npc = area.Default.Npcs.First(npc => npc.Id == 144);
        var location = new Location(area.Town, area.Area, checked((ushort)npc.X), checked((ushort)npc.Y));
        int[][] chains = [[2702, 11007, 11019, 11009], [2708, 11013, 11014, 11015], [2710, 10020, 10021], [2712, 11016, 11017, 11018]];
        var definitions = chains.SelectMany(chain => chain).Select(quests.Get).ToArray();
        foreach (byte type in new byte[] { 1, 2, 3, 4 })
        {
            int[] chain = chains[type - 1];
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters, resources,
                quests.Get(chain[0]), materialCount: 0, definitions: definitions, active: [], useCycles: true, resumeTown: true,
                configure: source => source with { Level = 20, Position = location,
                    InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 20,
                        source.InitialLearning with { Experience = growth.GetLevelStartExperience(20) }, learning) });
            Give([new(3037, 200), new(0, 1000000)]);
            Guid firstAcceptance = Guid.Empty;
            for (int index = 0; index < chain.Length; index++)
            {
                ushort id = checked((ushort)chain[index]);
                That(f.Change(id, QuestLifecycleAction.Accept) is { Outcome: QuestLifecycleOutcome.Committed },
                    "连续链按真实前置接取 " + id);
                var acceptance = f.Read().ActiveMainQuests.Single(row => row.QuestId == id).AcceptanceId;
                if (index == 0) firstAcceptance = acceptance;
                if (index == 1)
                    That(f.Change(id, QuestLifecycleAction.Condition, new(null, 0, 0)) is { Outcome: QuestLifecycleOutcome.Committed },
                        "在亚贝罗真实地图完成会面 " + id);
                else if (index == 2 && type != 3)
                {
                    Give([new(10400, 1)]);
                    var current = f.Session.Selected!;
                    int tool = type switch { 1 => 2600482, 2 => 2600463, _ => 2600474 };
                    var extractor = current.Inventory.Items.Single(item => item.Core.ItemId == tool);
                    var target = current.Inventory.Items.First(item => item.Core.ItemId == 10400);
                    That(ExpertJobCraft.TryPlanExtract(current, scripts, resources.Items, resources.GoldLimits, resources.EpicPieces,
                        extractor.Position.Index, 0, target.Position.Index, _ => 0, DateTimeOffset.UtcNow, out var plan, out _) &&
                        f.Session.CommitExpertJobCraft(plan.Inventory, plan.Experience).Accepted,
                        "真实萃取取得教学寻物材料 " + id);
                }
                if (index == chain.Length - 1) Teach();
                var finished = f.Finish(ushort.MaxValue, id);
                That(finished is { Outcome: QuestFinishOutcome.Committed } && f.Read().CompletedQuests.Contains(id) &&
                    f.Read().ActiveMainQuests.All(row => row.QuestId != id), "连续链真实交付并清活动槽 " + id);
                f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
                That(f.Read().CompletedQuests.Contains(id) && f.Session.Selected!.ExpertJobType == type,
                    "逐步重登恢复任务前置和副职业 " + id);
            }
            That(f.Receipts == chain.Length && f.Session.GiveUpExpertJob(scripts).Error == 0 &&
                definitions.All(quest => !f.Read().CompletedQuests.Contains(quest.Id)), "完整链放弃清理四职业关联记录");
            foreach (int[] candidate in chains)
                That(QuestEligibility.Evaluate(f.Read(), quests.Get(candidate[0]), quests).Eligible, "放弃后四职业均可重新选择");
            ushort restart = checked((ushort)chain[0]);
            That(f.Change(restart, QuestLifecycleAction.Accept) is { Outcome: QuestLifecycleOutcome.Committed } &&
                f.Read().ActiveMainQuests.Single().AcceptanceId != firstAcceptance &&
                f.Finish(ushort.MaxValue, restart).Outcome == QuestFinishOutcome.Committed && f.Receipts == chain.Length + 1,
                "重学使用新接取身份，旧领奖收据不挡新链也不重放奖励");

            void Give(InventoryReward[] rewards) => That(f.Session.MutateInventory(character =>
                InventoryRewardGrantService.TryGrantBatch(character, rewards, resources.Items, resources.GoldLimits, resources.EpicPieces)).Accepted,
                "连续链补给经真实库存入口");
            void Teach()
            {
                That(f.Session.OpenExpertJobStore(), "真实开店后执行末步教学");
                if (type is 2 or 4)
                {
                    uint recipe = type == 2 ? 2600149u : 2600083u;
                    var current = f.Session.Selected!;
                    That(ExpertJobCraft.TryPlan(current, scripts, resources.Items, resources.GoldLimits, resources.EpicPieces,
                        recipe, 1, ushort.MaxValue, _ => 0, DateTimeOffset.UtcNow, out var plan, out _) &&
                        f.Session.CommitExpertJobCraft(plan.Inventory, plan.Experience, f.Catalog).Accepted,
                        "炼金/控偶真实基础配方制作与教学一起提交");
                }
                else if (type == 1)
                {
                    Give([new(10015129, 1)]);
                    var scroll = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 10015129);
                    That(f.Session.LearnExpertJobRecipe(scroll.Position, 10015129, new(scripts, resources.Items)).Accepted,
                        "附魔链学习白卡卷轴");
                    var current = f.Session.Selected!;
                    var target = current.Inventory.Items.First(item => item.Core.ItemId == 18400);
                    var card = current.Inventory.Items.Single(item => item.Core.ItemId == 3619);
                    var plan = new ExpertJobEnchanting(scripts, resources.Items, resources.Enchantments,
                        new() { EnchantSuccessPercent = 100 }, random: _ => 0).Plan(current, 10015129, target.Position, card.Position);
                    That(plan is not null && ApplyTutorial(plan),
                        "附魔链真实扣卡材料、写效果、耐久与教学同事务");
                }
                else
                {
                    var current = f.Session.Selected!;
                    var source = current.Inventory.Items.First(item => item.Core.ItemId == 18400);
                    var service = new ExpertJobDisassembly(new(resources.Items, resources.Disjoint, resources.GoldLimits,
                        resources.EpicPieces, expertJobs: scripts), scripts, resources.Items, resources.GoldLimits,
                        resources.EpicPieces, new(), random: _ => 0);
                    var plan = service.Plan(current, source.Position);
                    That(plan is not null && ApplyTutorial(plan),
                        "分解链真实拆装、产物、经验、耐久与教学同事务");
                }
            }
            bool ApplyTutorial(ExpertJobActionPlan plan)
            {
                var before = f.Read();
                f.Sql("CREATE TRIGGER fail_tutorial BEFORE UPDATE OF condition_state ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'tutorial transaction failure'); END");
                Reject<SqliteException>(() => f.Session.CommitExpertJobAction(plan, f.Catalog),
                    "附魔/分解教程SQL失败必须拒绝整笔资产提交");
                var after = f.Read();
                That(after.Revision == before.Revision && after.Inventory.Gold == before.Inventory.Gold &&
                    after.Inventory.Items.SequenceEqual(before.Inventory.Items) && after.ExpertJobExperience == before.ExpertJobExperience &&
                    after.ExpertMachineEndurance == before.ExpertMachineEndurance && after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) &&
                    ReferenceEquals(f.Session.Selected, plan.Expected), "教程故障保持源UID、目标、材料、钱包、经验、耐久和会话快照");
                f.Sql("DROP TRIGGER fail_tutorial");
                return f.Session.CommitExpertJobAction(plan, f.Catalog).Accepted;
            }
        }
        Console.WriteLine($"PASS {Count - checks} 四职业自然任务链、逐步重登与放弃重学断言");
    }
}
