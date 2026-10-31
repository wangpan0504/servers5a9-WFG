using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestStandbyEntryTests
{
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters)
    {
        int before = Count;
        var quest = quests.Get(6220);
        That(QuestStandbyEntry.Supports(quest) && QuestHiddenEntry.Direct(quest, quests),
            "真实6220按原区域停留与hidden set形状进入隐藏候选");
        var fields = quest.Definition!.Fields;
        var sets = fields.Where(field => field.Name == "hidden set").ToArray();
        That(!QuestStandbyEntry.Supports(quest with { Definition = quest.Definition with { Fields = fields.Remove(sets[0]) } }) &&
            !QuestStandbyEntry.Supports(quest with { Definition = quest.Definition with { Fields = fields.Add(sets[0]) } }),
            "缺少或重复条件组不能按已确认入口处理");
        int[] done = towns.AccessQuestIds.Where(id => id is not (6220 or 6221 or 6222 or 6223 or 6224)).Append(6211).Distinct().ToArray();
        Location At(byte area)
        {
            var bounds = towns.Areas[((byte)60, area)].Map(done.ToHashSet()).Bounds
                .First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            return new(60, area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        }
        var clock = new ConsumableUseSessionTests.FakeClock();
        var location = At(1);
        using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false,
            level: 95, completed: done, position: location, timeProvider: clock);
        QuestLifecycleReceipt? Change(QuestLifecycleAction action) => f.Session.ChangeQuest(6220, action, quests,
            items.Items, items.GoldLimits, items.EpicPieces, action == QuestLifecycleAction.Condition ? new(null, 0, 0) : null);
        That(QuestEligibility.Candidates(f.Read(), quests).Contains(6220) && Change(QuestLifecycleAction.Accept)?.Commit is null,
            "6220可成为21候选但直接31不能绕过884");
        clock.Advance(TimeSpan.FromSeconds(1));
        That(f.Session.ReadHiddenQuest(6220, quests) is { Open: false }, "区域停留不替代真实通关历史");
        // 只构造隔离库的持久历史夹具；不将这项测试记作实际引导模式通关。
        f.Sql("""
            INSERT INTO character_dungeon_settlement_receipts(
              character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,dungeon_id,map_id,
              death_events,report_body,generation,base_experience,rank_experience,total_experience,rank_bonus_index,
              rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision)
            SELECT id,account,$p0,$p1,1,1,1,400000046,292106342,zeroblob(16),X'01',1,0,0,0,-1,
              'synthetic_history','quest_standby_test',95,95,0,0,revision FROM characters
            """, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        That(!f.Repository.HasQuestDungeonClear(f.Owner, f.Read(), 291100397, 1) &&
            f.Session.ReadHiddenQuest(6220, quests) is { Open: false }, "普通普雷历史不能冒充引导天空巢穴");
        f.Sql("UPDATE character_dungeon_settlement_receipts SET dungeon_id=291100397");
        var expected = f.Read();
        That(f.Repository.HasQuestDungeonClear(f.Owner, expected, 291100397, 1) &&
            !f.Repository.HasQuestDungeonClear(f.Owner, expected, 291100397, 2) &&
            !f.Repository.HasQuestDungeonClear(f.Owner, expected with { Revision = expected.Revision + 1 }, 291100397, 1) &&
            !f.Repository.HasQuestDungeonClear(f.Owner, expected with { Id = checked((ushort)(expected.Id + 1)) }, 291100397, 1),
            "历史读取保留角色、revision和真实结算次数");
        var opening = f.Session.ReadHiddenQuest(6220, quests);
        That(opening is { Open: true } && f.Read().Revision == expected.Revision,
            "原hidden set通关分支独立满足即可开放，不要求缺失的攻坚尝试分支");
        That(f.Session.Move(new(checked((ushort)(location.X + 1)), location.Y, 0, 0, 0, 0)) &&
            !f.Session.CanPublishQuestHidden(opening!) && Change(QuestLifecycleAction.Accept)?.Commit is null &&
            f.Session.ReadHiddenQuest(6220, quests) is { Open: true }, "同区移动不重计停留，但旧快照许可失效");
        // 60/1是攻坚专用场景，没有通往60/0的普通步行连接；仅在离线夹具准备另一区域快照。
        void ReselectAt(Location position)
        {
            f.Session.ReturnToRoster();
            var saved = f.Read();
            f.Repository.Save(f.Owner, saved, position, saved.Return);
            f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        }
        ReselectAt(At(0));
        clock.Advance(TimeSpan.FromSeconds(5));
        That(f.Session.ReadHiddenQuest(6220, quests) is { Open: false }, "其它区域停留不能满足60/1条件");
        ReselectAt(location);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        That(f.Session.ReadHiddenQuest(6220, quests) is { Open: false }, "重新入区未满1000毫秒不开放");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        That(f.Session.ReadHiddenQuest(6220, quests) is { Open: true }, "精确1000毫秒边界开放");
        f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        That(Change(QuestLifecycleAction.Accept)?.Commit is null && f.Session.ReadHiddenQuest(6220, quests) is { Open: false },
            "重选同角色清除旧许可并重新计时");
        clock.Advance(TimeSpan.FromSeconds(1));
        That(f.Session.ReadHiddenQuest(6220, quests) is { Open: true } && Change(QuestLifecycleAction.Accept)?.Commit is not null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == 6220).ConditionState == 1,
            "真实许可只接取6220，不补造会面完成");
        That(Change(QuestLifecycleAction.Condition)?.Commit is not null &&
            f.Session.FinishQuest(6220, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth)
                .Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(6220) &&
            QuestEligibility.Candidates(f.Read(), quests).Contains(6221), "实际NPC8001会面与34交付开启真实后继6221");
        Console.WriteLine($"PASS {Count - before} 真实6220区域、计时、合成历史及后继链断言（非引导实战/非实机）");
    }
}
