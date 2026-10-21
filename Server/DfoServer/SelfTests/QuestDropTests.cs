using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestDropTests
{
    static QuestField Field(string name, params int[] values) => new(name,values.Select(value => new QuestRewardToken(0,value,null)).ToImmutableArray(),[]);
    public static void RunCore()
    {
        int before = Count;
        var errors = ImmutableArray.CreateBuilder<string>();
        var rows = QuestDropDefinition.Read([Field("monster reward item",1,3,-1,10165055,2,50,5),
            Field("enemy reward item",13099,3,-1,2,10165055,1,100,-1),Field("clear reward item",3,-1,10165055,1,100,5)],errors);
        That(errors.Count == 0 && rows.Length == 3 && rows[0] == new QuestDropDefinition(1,1,3,-1,10165055,2,50,5) &&
            rows[1] == new QuestDropDefinition(13099,3,-1,2,10165055,1,100,-1) && rows[2].EnemyType == 0,
            "三类声明按资源原序完整解析，不把类型、难度和上限互换");
        That(rows[0].Matches(3,0,1,1,false) && !rows[0].Matches(4,0,1,1,false) && !rows[0].Matches(3,0,2,1,false) &&
            !rows[0].Matches(3,0,1,3,false),"掉落精确匹配模板及事件类型，地下城-1才是通配");
        That(rows[1].Matches(3,2,13099,3,false) && !rows[1].Matches(3,3,13099,3,false) &&
            !rows[2].Matches(3,0,1,1,false) && rows[2].Matches(3,0,1,1,true),"掉落难度精确匹配，通关奖励只由清图事实触发");
        foreach (var field in new[] { Field("monster reward item",1,3,-1,10165055,2,50),
            Field("enemy reward item",1,4,3,-1,10165055,1,100,5),Field("clear reward item",3,-1,10165055,1,101,5) })
        {
            errors.Clear(); QuestDropDefinition.Read([field],errors);
            That(errors.Count > 0,"坏掉落声明明确隔离，不能补默认行或截断为普通奖励");
        }
        PreloadScope();
        Console.WriteLine($"PASS {Count - before} 任务掉落资源行与作用域断言");
    }

    static void PreloadScope()
    {
        var items = new ItemCatalog(null!,null!,null!,0);
        items.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9001] = new(9001,ItemKind.Equipment,0,1,0,1,0,0,0,"",0,0,"unsupported"),
            [9002] = new(9002,ItemKind.Quest,0,1,0,99,0,0,0,"",0,0,"quest") });
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var distant = QuestEligibilityTests.Quest(29990) with
            { Definition = new([]) { Job = "[all]", Drops = [new(1,1,999,-1,9001,1,100,-1)] } };
        var local = QuestEligibilityTests.Quest(29991) with
            { Definition = new([]) { Job = "[all]", Drops = [new(1,1,3,-1,9002,1,100,-1)] } };
        var catalog = new QuestCatalog(new[] {distant,local}.ToDictionary(quest => quest.Id));
        var character = new Character(1,0,"Scope",1,new(38,2,450,240),null)
            { ActiveMainQuests = [new(0,29990,1),new(1,29991,1)] };
        var entry = new DungeonEntryPlan(1,1,3,0,byte.MaxValue,0,null!,null!,null!);
        var cancelled = QuestDropResources.Create(character with { ActiveMainQuests = [character.ActiveMainQuests[1]] },entry,catalog,items,pieces);
        That(cancelled.Items.ContainsKey(9002),"放弃无关任务后本图资源可装载");
        var loaded = QuestDropResources.Create(character,entry,catalog,items,pieces);
        That(loaded.Items.ContainsKey(9002) && !loaded.Items.ContainsKey(9001),"异图未支持掉落不污染当前入场预加载");
        Reject<NotSupportedException>(() => QuestDropResources.Create(character,entry with { DungeonId = 999 },catalog,items,pieces),
            "本图未支持持有域仍明确拒绝");
        var mixed = local with { Definition = local.Definition! with { Drops = [.. local.Definition.Drops,
            new(1,1,999,-1,9001,1,100,-1),new(1,1,3,2,9001,1,100,-1),new(1,2,-1,0,9002,1,100,-1),new(1,3,3,-1,9002,1,100,-1),new(0,0,-1,-1,9002,1,100,-1)] } };
        var parent = QuestEligibilityTests.Quest(29989) with { Condition = "[quest clear]", SubType = 2, IntData = [29991] };
        mixed = mixed with { MainQuest = parent.Id };
        catalog = new QuestCatalog(new[] {distant,mixed,parent}.ToDictionary(quest => quest.Id));
        character = character with { ActiveMainQuests = [new(0,29989,1),new(1,29990,1)] };
        That(QuestRelationProgress.SequentialParent(parent,catalog),"预加载夹具包含合法顺序父子定义");
        loaded = QuestDropResources.Create(character,entry,catalog,items,pieces);
        That(loaded.Quests.ContainsKey(29991) && loaded.Items.Keys.SequenceEqual(new[] {9002}),
            "顺序子任务保留本图及通配模板，错难度未支持行被隔离");
        Reject<NotSupportedException>(() => QuestDropResources.Create(character,entry with { Difficulty = 2 },catalog,items,pieces),
            "进入声明难度后未支持行仍拒绝");
        var accepted = new ActiveMainQuest(2,29991,1) { AcceptanceId = Guid.NewGuid() };
        var advanced = character with { ActiveMainQuests = [.. character.ActiveMainQuests,accepted] };
        var bound = entry with { QuestAcceptances = [accepted] };
        var identity = new DungeonRunIdentity(1,0,1,Guid.NewGuid(),1);
        foreach (int enemyType in new[] {1,2,3})
        {
            var reward = QuestDropPlan.Create(advanced,bound,loaded,Guid.NewGuid(),identity,1,1,enemyType,false,_ => 0,DateTimeOffset.UtcNow);
            That(reward.Outcomes is [{ Granted: 1 }] && reward.Inventory.After.Items.Single().Core.ItemId == 9002,
                "实例内新接顺序子任务的本图和通配奖励仍实际入包");
        }
        var clear = QuestDropPlan.Create(advanced,bound,loaded,Guid.NewGuid(),identity,1,1,1,true,_ => 0,DateTimeOffset.UtcNow);
        That(clear.Outcomes.Length == 2 && clear.Outcomes.All(row => row.Granted == 1),"通关行及当前怪物行均保留，异图错难度不产生奖励");
        foreach (var row in mixed.Definition!.Drops.Where(row => row.MatchesEntry(3,0)))
            That(loaded.Items.ContainsKey(row.ItemId),"怪物、APC、被动物件和通关行的本图模板完整预加载");
        var now = DateTimeOffset.UtcNow;
        var boosted = advanced with { Inventory = advanced.Inventory with
            { AccountWallet = advanced.Inventory.AccountWallet.ActivateContract(582, 86400, now) } };
        var oneDrop = mixed with { Definition = mixed.Definition! with { Drops = [new(1,1,3,-1,9002,1,100,-1)] } };
        var selected = loaded with { Quests = new Dictionary<int,StoryQuest> { [oneDrop.Id] = oneDrop } };
        QuestDropPlan Roll(Character actor, QuestDropResources facts, Func<int,int> random, DateTimeOffset? time = null) =>
            QuestDropPlan.Create(actor,bound,facts,Guid.NewGuid(),identity,1,1,1,false,random,time ?? now);
        That(Roll(boosted,selected,_ => 0).Outcomes.Single().Granted == 2 &&
            Roll(boosted,selected,_ => 1).Outcomes.Single().Granted == 1, "助手任务掉落奇数增量保持50%随机分支");
        That(Roll(advanced,selected,_ => throw new InvalidOperationException()).Outcomes.Single().Granted == 1 &&
            Roll(boosted,selected,_ => throw new InvalidOperationException(),now.AddDays(2)).Outcomes.Single().Granted == 1,
            "无契约及过期契约不增加掉落或抽取额外随机数");
        var material = selected with { Items = new Dictionary<int,ItemTemplate>
            { [9002] = selected.Items[9002] with { Kind = ItemKind.Material } } };
        That(Roll(boosted,material,_ => throw new InvalidOperationException()).Outcomes.Single().Granted == 1,
            "任务中的普通材料不享受任务栏物品加成");
        var cappedDrop = oneDrop with { Definition = oneDrop.Definition! with { Drops = [new(1,1,3,-1,9002,1,100,1)] } };
        That(Roll(boosted,selected with { Quests = new Dictionary<int,StoryQuest> { [cappedDrop.Id] = cappedDrop } },_ => 0)
            .Outcomes.Single() is { Rolled: 2, Granted: 1 }, "加成仍受资源持有上限约束");
        Reject<InvalidDataException>(() => Roll(boosted,selected,_ => 2), "非法额外随机值明确拒绝");

    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int before = Count;
        var original = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf); var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
        var seeking = QuestEligibilityTests.Quest(29987) with { Condition = "[seeking]", IntData = [10165055,2],
            Attributes = ["not give exp quest"], Definition = new([]) { Job = "[all]", Drops = [new(1,1,3,-1,10165055,2,50,5)] } };
        var catalog = new QuestCatalog(original.All.Append(seeking).ToDictionary(quest => quest.Id));
        foreach (bool lost in new[] { false,true })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,catalog,false,1,[],loseGrowth: lost);
            That(f.Session.LeaveRoom(),"掉落测试从实际出生房间回城");
            foreach (ushort id in new ushort[] {3145,29987})
                That(f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,catalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"任务掉落正常接取并保留独立身份");
            That(f.Session.Travel(new(38,2,1085,249,5,38,0,0)),"掉落测试进入实际副本入口");
            int draws = 0;
            That(f.Session.EnterFormalDungeon(3,3145,0,dungeons,rooms,drops,items.Items,Draw,items.EpicPieces) is not null,"任务所需物品在入场规划中装载");
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            var monster = receipt.Monsters.Monsters.First(monster => monster.IsBlocking && monster.MonsterId == 1);
            var observation = FormalDungeonSessionTests.Observation(receipt,monster);
            var online = f.Session.Selected!; var previous = f.Read();
            if (lost) Reject<IOException>(() => Observe(),"任务物品和死亡经验已提交但返回丢失");
            else
            {
                f.Sql($"UPDATE characters SET gold=gold+1 WHERE id={previous.Id}");
                Reject<InvalidOperationException>(() => Observe(),"同revision的库存变化仍拒绝旧任务掉落规划");
                That(QuestCompletionRepositoryTests.Same(previous with { Inventory = previous.Inventory with { Gold = previous.Inventory.Gold + 1 } },f.Read()) &&
                    ReferenceEquals(online,f.Session.Selected) && draws == 2,"拒绝保留外部金币变化，不保存任务物品或经验，不重抽已冻结奖励");
                f.Sql($"UPDATE characters SET gold=gold-1 WHERE id={previous.Id}");
                f.Sql("CREATE TRIGGER drop_abort AFTER INSERT ON character_quest_drop_receipts BEGIN SELECT RAISE(ABORT,'quest drop rollback'); END");
                Reject<SqliteException>(() => Observe(),"任务掉落回执末段失败");
                That(QuestCompletionRepositoryTests.Same(previous,f.Read()),"任务物品UID、寻物进度和死亡经验整笔回滚");
                f.Sql("DROP TRIGGER drop_abort");
            }
            That(ReferenceEquals(online,f.Session.Selected) && draws == 2,"结果未返回时保留旧在线快照和一次随机结果");
            var committed = Observe(); var actual = f.Read();
            That(committed.Outcome == DungeonRunOutcome.Accepted && draws == 2 && actual.Revision == previous.Revision + 1 &&
                actual.ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 0 &&
                actual.Inventory.Items.Single(item => item.Core.ItemId == 10165055) is { Uid: > 0, Core.Count: 2 },
                "精确重试复用随机结果，死亡经验、真实任务物品及寻物完成只增一次revision");
            string saved = JsonSerializer.Serialize(actual);
            That(Observe().Outcome == DungeonRunOutcome.Duplicate && JsonSerializer.Serialize(f.Read()) == saved && draws == 2,
                "重复死亡不重发任务物品、UID、经验或任务进度");
            var preloaded = QuestDropResources.Create(previous,receipt.Entry,catalog,items.Items,items.EpicPieces);
            var identity = receipt.Snapshot.Identity;
            var bound = receipt.Entry;
            var stale = previous with { ActiveMainQuests = previous.ActiveMainQuests.Select(row => row.QuestId == 29987 ? row with { AcceptanceId = Guid.NewGuid() } : row).ToArray() };
            var ignored = QuestDropPlan.Create(stale,bound,preloaded,Guid.NewGuid(),identity,receipt.Snapshot.CurrentMap.Id,1,1,false,Draw,DateTimeOffset.UtcNow);
            That(!ignored.Inventory.Changed && ignored.Outcomes.Length == 0 && draws == 2,"旧副本死亡不能向放弃重接后的新身份发任务物品");
            var capped = QuestDropPlan.Create(actual,bound,preloaded,Guid.NewGuid(),identity,receipt.Snapshot.CurrentMap.Id,1,1,false,Draw,DateTimeOffset.UtcNow);
            That(!capped.Inventory.Changed && draws == 2,"达到寻物需求后不继续超量发放，也不浪费随机结果");
            var template = items.Items.Get(10165055); var range = ItemSlotBoundService.Range(template.Kind,previous.Inventory.Expansion);
            var full = previous with { Inventory = previous.Inventory with { Items = previous.Inventory.Items.Concat(
                Enumerable.Range(range.Start,range.End - range.Start + 1).Select(slot => new InventoryItem(slot + 1000,new(InventoryListType.Main,(short)slot),
                    InventoryRewardGrantService.Create(template) with { ItemId = 99990123 }))).ToImmutableArray() } };
            var refused = QuestDropPlan.Create(full,bound,preloaded,Guid.NewGuid(),identity,receipt.Snapshot.CurrentMap.Id,1,1,false,Draw,DateTimeOffset.UtcNow);
            That(!refused.Inventory.Changed && refused.Outcomes is [{ Rolled: 2, Granted: 0, Error: 17 }],"满任务页记录未入库结果，不能生成寻物进度或丢弃既有物品");
            f.Session.ReturnFromFormalDungeon(); f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 10165055).Uid == actual.Inventory.Items.Single(item => item.Core.ItemId == 10165055).Uid &&
                f.Session.Selected.ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 0,"重登恢复同一任务物品UID和实际寻物状态");
            int Draw(int limit) { if (limit == 100) draws++; return 0; }
            FormalDungeonDeathResult Observe() => f.Session.ObserveFormalDungeonDeath(observation,kills,growth);
        }
        foreach (bool lost in new[] {false,true})
        {
            var passive = seeking with { Definition = seeking.Definition! with { Drops = [new(13099,3,3,-1,10165055,2,100,2)] } };
            var passiveCatalog = new QuestCatalog(original.All.Append(passive).ToDictionary(quest => quest.Id));
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,passiveCatalog,lost);
            f.Session.LeaveRoom();
            foreach (ushort id in new ushort[] {3146,29987})
                That(f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,passiveCatalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"被动物件任务掉落接取");
            f.Session.Travel(new(38,2,1085,249,5,38,0,0));
            That(f.Session.EnterFormalDungeon(3,3146,0,dungeons,rooms,drops,items.Items,_ => 0,items.EpicPieces) is not null,"被动物件掉落绑定真实父任务地图");
            f.Session.CompleteFormalDungeonLoading(); var beforePassive = f.Read();
            if (lost) Reject<IOException>(() => ObservePassive(),"被动物件物品已入库但返回丢失");
            else
            {
                f.Sql("CREATE TRIGGER passive_drop_abort AFTER INSERT ON character_quest_lifecycle_receipts WHEN NEW.scene_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'passive drop rollback'); END");
                Reject<SqliteException>(() => ObservePassive(),"被动物件掉落与场景回执事务末段失败");
                That(QuestCompletionRepositoryTests.Same(beforePassive,f.Read()),"被动物件掉落、寻物进度和场景回执整笔回滚");
                f.Sql("DROP TRIGGER passive_drop_abort");
            }
            var progress = ObservePassive();
            That(progress?.Commit is { ProjectionCurrent: true } && f.Read().Inventory.Items.Single(item => item.Core.ItemId == 10165055).Core.Count == 2 &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 0 && f.Read().Learning.Experience == beforePassive.Learning.Experience &&
                ObservePassive() is null,"真实被动物件只发一次任务物品，进度和回执原子保存，无伪造死亡经验");
            DfoServer.Game.Session.QuestLifecycleReceipt? ObservePassive() => f.Session.ObserveFormalPassiveDeath(13099,true,new string('E',64),rooms);
        }
        var clearing = seeking with { Definition = seeking.Definition! with { Drops = [new(0,0,3,-1,10165055,2,100,2)] } };
        var clearCatalog = new QuestCatalog(original.All.Append(clearing).ToDictionary(quest => quest.Id));
        using (var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,clearCatalog,false,1,[]))
        {
            f.Session.LeaveRoom();
            foreach (ushort id in new ushort[] {3145,29987}) f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,clearCatalog,items.Items,items.GoldLimits,items.EpicPieces);
            f.Session.Travel(new(38,2,1085,249,5,38,0,0));
            var opening = f.Session.EnterFormalDungeon(3,0,0,dungeons,rooms,drops,items.Items,_ => 0,items.EpicPieces)!;
            That(opening.Entry is { QuestId: 0, RouteQuestId: 3145, ActiveQuestSlot: byte.MaxValue } && opening.Entry.QuestAcceptanceId == Guid.Empty,
                "普通选图入口按唯一活动剧情选择任务路线，实例仍绑定全部真实接取身份");
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var point in path)
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                {
                    var result = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                    That(result.Outcome == DungeonRunOutcome.Accepted && f.Read().Inventory.Items.Any(item => item.Core.ItemId == 10165055) == (result.ClearFact is not null),
                        "通关声明只在权威末次Boss死亡事务发物品，普通死亡不提前赠送");
                }
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path,point)+1]; f.Session.MoveFormalDungeon((byte)next.X,(byte)next.Y);
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(f.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 0 &&
                f.Session.ReadClearedQuest(3145)?.Clear is not null,"通关物品、寻物状态和清图凭据共同保存");
        }
        Console.WriteLine($"PASS {Count - before} 任务掉落、满包、接取隔离、死亡及物件事务回滚与恢复断言（非实机）");
    }
}
