using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonAltarSessionTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var archive = resources.Archive;
        var dungeons = SelfTestCatalogs.Dungeons(archive);
        var rooms = SelfTestCatalogs.Rooms(archive);
        var growth = SelfTestCatalogs.Growth(archive);
        var items = resources.ItemResources;
        var policy = new DungeonAltarRewardPolicy(DfoServer.GameWorld.Dungeons.DungeonAltarRewardCatalog.Load(archive),
            DfoServer.GameWorld.Dungeons.DungeonAltarClearItemCatalog.Load(archive), items.DungeonAltarItems,
            SelfTestCatalogs.KillExperience(archive), items.Items, items.GoldLimits, items.EpicPieces);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        // 原生选图会自动携带关联支线任务；不能只测手工清零任务号的请求。
        foreach (var (dungeonId, questId, prerequisite) in new[] { (11006u, (ushort)5650, 63), (11007u, (ushort)7340, 7339) })
        {
            var clock = new Clock();
            using var linked = New(clock, done.Append(prerequisite).Distinct().ToArray());
            var accepted = linked.Session.ChangeQuest(questId, DfoServer.Game.Quests.QuestLifecycleAction.Accept,
                resources.Quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null, "合法接取祭坛关联支线：" + questId);
            That(linked.Session.BeginDungeonSelection() is not null, "关联任务祭坛建立原选图上下文");
            That(linked.Session.EnterFormalDungeon(dungeonId, (uint)(questId + 1), 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                linked.Session.EnterFormalDungeon(dungeonId, questId, 1, dungeons, rooms, pieces: items.EpicPieces) is null &&
                linked.Session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.All(row => row.Daily == 3) &&
                linked.Session.Selected!.Inventory.Gold == 500000 && linked.Session.DungeonSnapshot is null,
                "未接取任务或祭坛不支持的难度仍拒绝，不扣费扣次或安装实例");
            var entered = linked.Session.EnterFormalDungeon(dungeonId, questId, 0, dungeons, rooms, pieces: items.EpicPieces);
            That(entered is not null, $"关联任务不阻断祭坛准入：{questId}/{linked.Session.LastFormalDungeonRejection}");
            That(entered!.Monsters.Altar is not null && entered.Entry.QuestId == questId && entered.Entry.RouteQuestId == 0 &&
                entered.Entry.QuestAcceptanceId == linked.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == questId).AcceptanceId,
                "祭坛复用普通任务路线资格并冻结原接取，仍使用专用波次与次数事务");
            if (dungeonId == 11007)
            {
                var hunts = resources.Quests.All.Where(quest => quest.Condition == "[hunt monster]" &&
                    (quest.Id == 5650 || quest.DungeonIndex is 11006 or 11007 || quest.IntData.Count >= 4 && quest.IntData[0] is 11006 or 11007)).ToArray();
                That(hunts.Select(quest => quest.Id).Order().SequenceEqual(new[] { 5650, 7340, 7341, 7343, 7344, 7345 }),
                    "正式任务索引可加载的六条祭坛猎杀声明完整覆盖");
                foreach (var quest in hunts)
                {
                    bool supported = QuestHuntProgress.TryTargets(quest, out var targets);
                    That(supported == (quest.Grade != "[training]"), "祭坛同类普通猎杀声明可消费，training不扩大准入：" + quest.Id);
                    if (!supported) continue;
                    var active = new ActiveMainQuest(0, checked((ushort)quest.Id), QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
                    var entry = entered.Entry with { DungeonId = quest.IntData[0] == 11007 ? 11007u : 11006u, QuestAcceptances = [active] };
                    var remaining = ImmutableArray.Create(active);
                    foreach (var hunt in targets)
                    {
                        var monster = new DungeonPlannedMonster(0, 100, hunt.MonsterId, 70, null);
                        That(QuestHuntProgress.MonsterDeath(remaining, entry with { QuestAcceptances = [] }, resources.Quests, monster)
                                .SequenceEqual(remaining), "祭坛同类目标仍只消费原接取身份");
                        remaining = QuestHuntProgress.MonsterDeath(remaining, entry, resources.Quests, monster);
                    }
                    That(remaining.Single().ConditionState == 0, "祭坛各真实目标完整推进含木乃伊双通道：" + quest.Id);
                }
            }
            var target = resources.Quests.Get(questId).IntData[2];
            bool targetKilled = false;
            while (!targetKilled)
            {
                var loaded = linked.Session.CompleteFormalDungeonLoading()!;
                That(linked.Session.PrepareDungeonAltarUi(loaded), "任务祭坛沿真实加载准备波次");
                var current = linked.Session.StartDungeonAltar(loaded)!;
                while (!current.State.Completed && !targetKilled)
                {
                    if (current.State.WaitingForDifficulty)
                        current = linked.Session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
                    clock.AdvanceTo(current.Due!.Value);
                    current = linked.Session.TickDungeonAltar(current)!;
                    if (current.Batch is not { } batch) continue;
                    foreach (var actor in batch.Actors)
                    {
                        var beforeDeath = linked.Read();
                        bool matches = actor.Phase.MonsterId == target;
                        if (matches)
                        {
                            var selected = linked.Session.Selected;
                            var snapshot = linked.Session.DungeonSnapshot;
                            linked.Sql("CREATE TRIGGER fail_altar_hunt BEFORE UPDATE ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'altar hunt rollback'); END");
                            Reject<Microsoft.Data.Sqlite.SqliteException>(() =>
                                linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth),
                                "祭坛目标任务写入失败保留原死亡候选");
                            That(linked.Read().Revision == beforeDeath.Revision &&
                                ReferenceEquals(linked.Session.Selected, selected) && linked.Session.DungeonSnapshot!.Identity == snapshot!.Identity &&
                                linked.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(snapshot.LivingMonsters) &&
                                linked.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 1,
                                "祭坛目标SQL失败不提交revision、条件或内存死亡");
                            linked.Sql("DROP TRIGGER fail_altar_hunt");
                        }
                        current = linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth)!;
                        var afterDeath = linked.Read();
                        That(afterDeath.ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == (matches ? 0u : 1u),
                            "祭坛只按真实目标击杀持久推进猎杀条件，不把通关或任意小怪当目标：" + questId);
                        That(afterDeath.Learning.Experience == beforeDeath.Learning.Experience &&
                            afterDeath.Level == beforeDeath.Level && afterDeath.Skills.SequenceEqual(beforeDeath.Skills) &&
                            afterDeath.Learning.Trees.Zip(beforeDeath.Learning.Trees).All(pair =>
                                pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                                pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second)) &&
                            afterDeath.DungeonState == beforeDeath.DungeonState && afterDeath.Inventory.Gold == beforeDeath.Inventory.Gold &&
                            afterDeath.Inventory.Items.SequenceEqual(beforeDeath.Inventory.Items),
                            "祭坛任务死亡不授普通经验、技能点、熟练度、金币或掉落");
                        if (!matches) continue;
                        targetKilled = true;
                        That(current.QuestCharacter is not null && linked.Session.CanPublishDungeonAltar(current) &&
                            linked.Session.MarkDungeonAltarQuestsPublished(current),
                            "同一原对象SQL重试成功后提供已提交任务投影并可确认发送");
                        long revision = afterDeath.Revision;
                        That(linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth) is not null &&
                            linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey], new string('B',64), growth) is null &&
                            linked.Read().Revision == revision,
                            "任务目标39与255混合重报不重复计数或递增revision");
                        break;
                    }
                }
                if (!targetKilled)
                {
                    var next = entered.Entry.Maze.StaticTopology.Links!.SelectMany(link =>
                        new[] { (From: link.First, To: link.Second), (From: link.Second, To: link.First) })
                        .Single(edge => edge.From == loaded.Snapshot.CurrentMap.Position && edge.To.X > edge.From.X).To;
                    That(linked.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                        "未出现目标前按真实祭坛连接推进下一房间");
                }
            }
            That(linked.Session.ReturnFromFormalDungeon() is not null &&
                linked.Repository.LoadCharacter(linked.Owner,0).ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 0,
                "已提交的祭坛目标进度退出并重读后保留，不依赖最终奖励结算");
            var finished = linked.Session.FinishQuest(questId, ushort.MaxValue, resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(archive), items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(finished.Receipt is not null && linked.Read().CompletedQuests.Contains(questId) &&
                linked.Read().ActiveMainQuests.All(row => row.QuestId != questId),
                "祭坛任务沿原领奖事务完成并移出活动列表：" + questId);
        }
        foreach (bool lost in new[] { false, true })
        {
            var clock = new Clock();
            using var linked = New(clock, done.Append(5650).Distinct().ToArray(), lost);
            That(linked.Session.ChangeQuest(5651, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
                "真实5651寻物支线可合法接取");
            That(linked.Session.BeginDungeonSelection() is not null, "5651建立原生祭坛选图上下文");
            var entered = linked.Session.EnterFormalDungeon(11006, 5651, 0, dungeons, rooms,
                drops: SelfTestCatalogs.MonsterDrops(archive), items: items.Items, draw: _ => 0, pieces: items.EpicPieces);
            That(entered is not null, "接取5651后仍可进入无尽祭坛：" + linked.Session.LastFormalDungeonRejection);
            var loaded = linked.Session.CompleteFormalDungeonLoading()!;
            That(linked.Session.PrepareDungeonAltarUi(loaded), "寻物祭坛加载原生UI");
            var current = linked.Session.StartDungeonAltar(loaded)!;
            That(current.State.Plan.Actors.Any(actor => actor.Phase.MonsterId == 56004), "真实祭坛房间包含任务目标史莱姆王");
            bool found = false;
            while (!found && !current.State.Completed)
            {
                clock.AdvanceTo(current.Due!.Value);
                current = linked.Session.TickDungeonAltar(current)!;
                if (current.Batch is not { } batch) continue;
                foreach (var actor in batch.Actors)
                {
                    if (actor.Phase.MonsterId != 56004)
                    {
                        current = linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth)!;
                        continue;
                    }
                    var beforeDeath = linked.Read();
                    var selected = linked.Session.Selected;
                    if (lost)
                    {
                        Reject<IOException>(() => linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth),
                            "祭坛任务材料已提交但返回丢失");
                        That(ReferenceEquals(selected, linked.Session.Selected) && Held() == 1,
                            "未知结果保留原在线快照及已落库任务材料");
                    }
                    else
                    {
                        linked.Sql("CREATE TRIGGER fail_altar_drop AFTER INSERT ON character_quest_drop_receipts BEGIN SELECT RAISE(ABORT,'altar drop rollback'); END");
                        Reject<Microsoft.Data.Sqlite.SqliteException>(() => linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth),
                            "祭坛材料回执晚期SQL失败");
                        That(Held() == 0 && linked.Read().Revision == beforeDeath.Revision && ReferenceEquals(selected, linked.Session.Selected),
                            "任务材料、条件、死亡与revision全部回滚");
                        linked.Sql("DROP TRIGGER fail_altar_drop");
                    }
                    current = linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth)!;
                    var committed = linked.Read();
                    That(Held() == 1 && committed.ActiveMainQuests.Single(row => row.QuestId == 5651).ConditionState == 0 &&
                        current.QuestCharacter == linked.Session.Selected && current.QuestDropSlots.Length == 1 &&
                        current.QuestCharacter!.Inventory.Find(current.QuestDropSlots[0]) is { Uid: > 0, Core.ItemId: 4363 },
                        "祭坛真实目标死亡后提交材料实际UID并提供库存/任务投影");
                    That(committed.Revision == beforeDeath.Revision + 1 && committed.Learning.Experience == beforeDeath.Learning.Experience &&
                        committed.Inventory.Gold == beforeDeath.Inventory.Gold,
                        "祭坛材料与任务共用一次revision，不额外授予普通经验或金币");
                    That(linked.Session.ObserveDungeonAltarDeaths([actor.ObjectKey], new string('B', 64), growth) is not null &&
                        linked.Session.ObserveDungeonAltarDeath(Observation(actor.ObjectKey), growth) is not null &&
                        Held() == 1 && linked.Read().Revision == committed.Revision,
                        "39/255交叉重报和未知结果恢复不重复材料或任务进度");
                    found = true;
                    break;
                }
            }
            That(found && linked.Session.ReturnFromFormalDungeon() is not null, "取得任务材料后可按原生路径回城");
            var finished = linked.Session.FinishQuest(5651, ushort.MaxValue, resources.Quests,
                SelfTestCatalogs.QuestRewardParameters(archive), items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(finished.Receipt is not null && linked.Read().CompletedQuests.Contains(5651) && Held() == 0,
                "5651按实际材料交付，完成任务后精确扣除碎片");
            int Held() => linked.Read().Inventory.Items.Where(item => item.Core.ItemId == 4363).Sum(item => item.Core.Count);
        }
        foreach (uint dungeonId in new uint[] { 11006, 11007 })
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            if (dungeonId == 11006)
            {
                int fee = dungeons.AltarAdmission.BloodFee(session.Selected!.Level, 0);
                That(session.MutateInventory(c => CurrencyService.SetGold(c, checked((uint)(fee - 1)))).Accepted,
                    "准备有次数但少一金币的无尽角色");
                var unpaid = session.Selected!;
                That(session.BeginDungeonSelection() is not null &&
                    session.EnterFormalDungeon(11006, 0, 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                    session.LastFormalDungeonRejection == "altar-entry-insufficient-gold" &&
                    ReferenceEquals(session.Selected, unpaid) && fixture.Read().Inventory.Gold == fee - 1 &&
                    session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == 11006).Daily == 3,
                    "无尽次数不是免费资格，费用不足保留选图及全部次数，不安装Run");
                That(session.DungeonSnapshot is null && session.ReturnFromDungeonSelection() is not null &&
                    session.MutateInventory(c => CurrencyService.SetGold(c, 500000)).Accepted,
                    "失败后可退出选图补足费用，不残留半个实例");
            }
            var paid = session.Selected!;
            var countsBefore = session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!;
            That(countsBefore.Counts.All(row => row.Daily == 3 && row.Weekly == 0) &&
                session.CanPublishDungeonEnterCounts(countsBefore), "祭坛空账本投影两条3次，回执绑定当前选角");
            fixture.Sql("CREATE TRIGGER fail_count_entry BEFORE INSERT ON dungeon_special_entry_receipts BEGIN SELECT RAISE(ABORT,'count rollback'); END");
            try
            {
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => Enter(session, dungeonId), "实际入场回执失败回滚计数和扣费");
                That(session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.All(row => row.Daily == 3) &&
                    session.Selected!.Inventory.Gold == paid.Inventory.Gold && session.CanPublishDungeonEnterCounts(countsBefore),
                    "SQL回滚后仍是原角色快照与3次，不投影未提交的扣次");
            }
            finally { fixture.Sql("DROP TRIGGER fail_count_entry"); }
            var entered = Enter(session, dungeonId);
            That(!session.CanPublishDungeonEnterCounts(countsBefore) &&
                session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == dungeonId).Daily == 2,
                "原扣费事务提交后投影2次，旧角色快照不能再发布3次");
            That(entered.Monsters.Altar is not null && entered.SpecialEntry is not null &&
                entered.Character.Inventory.Gold == paid.Inventory.Gold - entered.SpecialEntry.Gold &&
                entered.Character.Fatigue == paid.Fatigue, "实际祭坛准入使用专用演员，扣费与次数共提交而不扣疲劳");
            int roomsCleared = 0, actorsKilled = 0;
            while (true)
            {
                var loaded = session.CompleteFormalDungeonLoading()!;
                That(session.StartDungeonAltar(loaded) is null && session.PrepareDungeonAltarUi(loaded),
                    "实际房间必须完成UI准备才能启动波次");
                var current = session.StartDungeonAltar(loaded)!;
                var nextRooms = entered.Entry.Maze.StaticTopology.Links!.SelectMany(link =>
                    new[] { (From:link.First,To:link.Second), (From:link.Second,To:link.First) })
                    .Where(edge => edge.From == loaded.Snapshot.CurrentMap.Position && edge.To.X > edge.From.X)
                    .Select(edge => edge.To).ToArray();
                var firstActor = loaded.Monsters.Altar!.Actors[0];
                That(session.ObserveDungeonAltarDeath(Observation(firstActor.ObjectKey), growth) is null,
                    "两祭坛39不能确认已分配但尚未计时发布的对象");
                while (!current.State.Completed)
                {
                    if (current.State.WaitingForDifficulty)
                        current = session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
                    clock.AdvanceTo(current.Due ?? throw new InvalidOperationException("祭坛尚未完成但没有后续计时"));
                    current = session.TickDungeonAltar(current) ?? throw new InvalidOperationException("实际波次到期没有生成");
                    if (current.Batch is { } batch)
                    {
                        actorsKilled += batch.Actors.Length;
                        var keys = batch.Actors.Select(row => row.ObjectKey).ToImmutableArray();
                        if (dungeonId == 11007 || current.State.Round == current.State.Plan.Definition.RoundCount - 1 && current.State.NextDue is null)
                        {
                            if (nextRooms is [var pendingRoom])
                                That(session.MoveFormalDungeon(checked((byte)pendingRoom.X), checked((byte)pendingRoom.Y)) is null,
                                    "无尽末批已经发布但尾怪尚未确认死亡时仍不能提前换房");
                            foreach (var actor in batch.Actors)
                            {
                                var observation = Observation(actor.ObjectKey);
                                var zero = new NativeDungeonScriptedDeathObservation(actor.ObjectKey,1,true,new string('C',64));
                                That(session.ObserveDungeonAltarScriptedDeath(zero with { ChecksumMatches = false }, growth) is null &&
                                    session.ObserveDungeonAltarScriptedDeath(zero with { MonsterKey = uint.MaxValue }, growth) is null &&
                                    session.ObserveDungeonAltarScriptedDeath(zero with { MonsterKey = ushort.MaxValue }, growth) is null,
                                    "祭坛零参与者39仍拒绝错误摘要、溢出键和未发布对象");
                                That(session.ObserveDungeonAltarDeath(observation with { ChecksumMatches = false }, growth) is null &&
                                    session.ObserveDungeonAltarDeath(observation with { MonsterKey = uint.MaxValue }, growth) is null &&
                                    session.ObserveDungeonAltarDeath(observation with { MonsterKey = ushort.MaxValue }, growth) is null,
                                    "祭坛39拒绝错误校验和、溢出键与未知对象，仍可处理后续合法击杀");
                                current = session.ObserveDungeonAltarDeath(observation, growth)
                                    ?? throw new InvalidOperationException("祭坛实际39死亡未推进");
                                var deathReplay = session.ObserveDungeonAltarDeath(observation, growth);
                                var zeroReplay = session.ObserveDungeonAltarScriptedDeath(zero, growth);
                                That(deathReplay is { RoundAdvanced: false } && deathReplay.Round == current.Round &&
                                    zeroReplay is { RoundAdvanced: false } && zeroReplay.Round == current.Round &&
                                    ReferenceEquals(session.Selected, entered.Character),
                                    "祭坛重复39只补发退场资格，不重复推进或写普通成长/掉落");
                            }
                            That(session.ObserveDungeonAltarDeaths(keys, new string('B',64), growth) is null,
                                "39之后重复255共用原死亡表，不推进另一回合");
                        }
                        else
                        {
                            current = session.ObserveDungeonAltarDeaths(keys, new string('A',64), growth)
                                ?? throw new InvalidOperationException("实际祭坛对象死亡未推进");
                            That(session.ObserveDungeonAltarDeath(Observation(keys[0]), growth) is { RoundAdvanced: false },
                                "255之后重报39可补发退场资格，不因上报协议不同重复推进");
                        }
                    }
                }
                roomsCleared++;
                if (session.Phase == SessionPhase.Settlement) break;
                var next = nextRooms.Single();
                That(session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,
                    "无尽必须清完本房五回合才能沿实际地图连接换房");
                That(!session.CanPublishDungeonAltar(current) && session.TickDungeonAltar(current) is null &&
                    session.ObserveDungeonAltarDeath(Observation(current.State.Plan.Actors[^1].ObjectKey), growth) is null,
                    "换房后旧房间的计时、尾怪重报及发布资格全部失效，不阻断新房加载");
            }
            var result = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(result.Result is { Stage: 0 } && result.Result.Pending.Fact.CompletedRounds == (dungeonId == 11006 ? 15 : 7) &&
                roomsCleared == (dungeonId == 11006 ? 3 : 1) && actorsKilled > 0,
                "实际十五或七回合只进入专用结算，不走普通翻牌");
            long revision = fixture.Read().Revision;
            var replay = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(replay.Result!.Commit.Duplicate == result.Result!.Commit.Duplicate && fixture.Read().Revision == revision &&
                ReferenceEquals(replay.Result.Pending,result.Result.Pending), "重复结算复用冻结奖励，不重抽或二次发奖");
            That(!session.MutateInventory(c => CurrencyService.SetGold(c,1), requireTown:false).Accepted,
                "结算演出前不能并发修改已提交但待投影资产");
            for (int stage = 1; stage <= 2; stage++)
            {
                That(session.TickDungeonAltar(result)?.Result?.Stage == stage-1,
                    "结算计时提前唤醒只续等，不推进演出阶段");
                clock.AdvanceTo(result.Due!.Value);
                result = session.TickDungeonAltar(result)!;
                That(result.Result?.Stage == stage, "祭坛奖励及到账按八秒阶段推进");
            }
            clock.AdvanceTo(result.Due!.Value);
            That(session.TickDungeonAltar(result)?.Returned is not null && session.Phase == SessionPhase.Town &&
                fixture.Read().Revision >= revision, "专用结算结束后回原城镇并保留已到账资产");
            Enter(session, dungeonId);
            var reloaded = session.CompleteFormalDungeonLoading()!;
            That(session.PrepareDungeonAltarUi(reloaded), "再次挑战使用新实例的UI准备身份");
            var abandoned = session.StartDungeonAltar(reloaded)!;
            That(session.ReturnFromFormalDungeon() is not null && !session.CanPublishDungeonAltar(abandoned) &&
                session.TickDungeonAltar(abandoned) is null && session.DungeonSnapshot is null &&
                session.ObserveDungeonAltarDeath(Observation(entered.Monsters.Altar!.Actors[0].ObjectKey), growth) is null &&
                session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == dungeonId).Daily == 1,
                "祭坛已启动波次退出后旧计时不能发布或复活实例，已提交的两次入场不退款");
            session.End();
            That(!session.CanPublishDungeonAltar(abandoned) && session.TickDungeonAltar(abandoned) is null,
                "会话结束后原祭坛回调继续失效");
        }
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            session.MutateInventory(c => new(true,0,new(c.Inventory,c.Inventory with { ReviveCoins = 20 },"fixture_coins")));
            Enter(session,11007);
            var loaded = session.CompleteFormalDungeonLoading()!;
            That(session.CurrentDungeonAltarResult() is null, "尚未提交祭坛结算时不能手动完成结果页");
            session.PrepareDungeonAltarUi(loaded);
            var active = session.StartDungeonAltar(loaded)!;
            DungeonAltarReceipt? old = null;
            for (int count = 0; count < 7; count++)
            {
                That(session.ReportFormalCharacterDeath() is not null,"祭坛实际死亡进入复活窗口");
                var waiting = session.WaitDungeonAltarRevival()!;
                That(session.BeginDungeonAltarSettlement(policy,growth) is null && !session.CanFinishDungeonAltarDeath(waiting),
                    "十秒前不能通过死亡或255重发提前结算");
                That(session.ReviveFormalCharacter(session.Selected!.Id)?.Accepted == true &&
                    session.ResumeDungeonAltar() is not null && !session.CanFinishDungeonAltarDeath(waiting),
                    "扣币成功恢复原祭坛，旧死亡回调不能结算新生命");
                old = waiting;
            }
            session.ReportFormalCharacterDeath();
            var final = session.WaitDungeonAltarRevival()!;
            That(session.ReviveFormalCharacter(session.Selected!.Id)?.Accepted == false &&
                session.Selected.Inventory.ReviveCoins == 13, "当前PVF极限七币上限生效，第八次不扣币");
            clock.AdvanceTo(final.Due!.Value);
            That(!session.CanFinishDungeonAltarDeath(old!) && session.CanFinishDungeonAltarDeath(final),
                "同实例早先死亡序号失效，只有本次十秒截止能失败结算");
            var result = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(result.Result?.Pending.Fact is { Cleared:false, CompletedRounds:0 },
                "十秒未复活才冻结零回合失败，没有虚构通关");
            long revision = fixture.Read().Revision;
            var stage0 = session.CurrentDungeonAltarResult()!;
            var stage1 = session.AdvanceDungeonAltarResult(stage0)!;
            That(stage0.Result?.Stage == 0 && stage1.Result?.Stage == 1 &&
                !session.CanPublishDungeonAltar(stage0) && session.AdvanceDungeonAltarResult(stage0) is null,
                "手动回城立即推进排名阶段，旧回执不能重复推进或发布");
            var stage2 = session.AdvanceDungeonAltarResult(stage1)!;
            That(stage2.Result?.Stage == 2 && session.CurrentDungeonAltarResult()?.Result?.Stage == 2 &&
                !session.CanPublishDungeonAltar(stage1) && session.AdvanceDungeonAltarResult(stage1) is null &&
                session.AdvanceDungeonAltarResult(stage2) is null && fixture.Read().Revision == revision,
                "手动回城依次推进奖励和资产投影，重放不重复写入奖励");
            That(session.ReturnFromFormalDungeon() is not null && session.Phase == SessionPhase.Town &&
                session.CurrentDungeonAltarResult() is null && !session.CanPublishDungeonAltar(stage2) &&
                session.TickDungeonAltar(stage2) is null && fixture.Read().Revision >= revision,
                "手动回城提交后旧结算和自动计时失效");
        }
        Console.WriteLine($"PASS {Count-before} 实际祭坛入口、全波次、专用结算、复活窗口和币上限断言");

        QuestSceneTests.Fixture New(Clock clock, int[]? completed = null, bool loseGrowth = false)
        {
            var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,completed ?? done,loseGrowth:loseGrowth,position:position,timeProvider:clock);
            That(fixture.Session.MutateInventory(c => CurrencyService.SetGold(c,500000)).Accepted,"准备隔离角色祭坛费用");
            return fixture;
        }
        FormalDungeonReceipt Enter(GameSession session,uint id)
        {
            That(session.BeginDungeonSelection() is not null,"实际亡者峡谷建立选图上下文");
            return session.EnterFormalDungeon(id,0,0,dungeons,rooms,items: null,draw:_=>0,pieces:items.EpicPieces)
                ?? throw new InvalidOperationException("祭坛实际准入失败："+session.LastFormalDungeonRejection);
        }

        NativeDungeonDeathObservation Observation(ushort key) => new(key, 1, 0x111, 1, 600, 1, 0, 70, 0, 650, 1,
            true, new string('A',64));
    }

    sealed class Clock : TimeProvider
    {
        long elapsed;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1792756800).AddMilliseconds(elapsed);
        public void AdvanceTo(long value) => elapsed = Math.Max(elapsed,value);
    }
}
