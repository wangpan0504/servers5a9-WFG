using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Raids;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class QuestAntonPhaseProgressTests
{
    static StoryQuest Quest() => QuestEligibilityTests.Quest(29080) with
    { Grade = "[side]", Npc = 0, CompleteNpc = 0, Condition = "[raid phase clear]", SubType = 0, IntData = new[] { 0, 0, 5, -1, 1, 0, 5, -1 } };

    internal static void RunCore()
    {
        int checks = Count;
        var quest = Quest(); var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [quest.Id] = quest });
        var active = new ActiveMainQuest(3, (ushort)quest.Id, QuestRaidPhaseProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
        var bound = QuestRaidPhaseProgress.Bindings([active], catalog);
        var title = quest with { Id = 29081, Grade = "[title]", IntData = new[] { 0, 0, 5, 1 } };
        var mixedCatalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [quest.Id] = quest, [title.Id] = title });
        var titleActive = new ActiveMainQuest(4, (ushort)title.Id, 5) { AcceptanceId = Guid.NewGuid() };
        That(GameSession.CanTriggerNativeAntonQuests([active], catalog) &&
            !GameSession.CanTriggerNativeAntonQuests([active, titleActive], mixedCatalog),
            "普通支持任务可确认原生事件，混有未接入称号或身份声明必须601F0，已支持任务仍由服务端33同步");
        That(active.ConditionState == 5u + (5u << 9) && bound.SequenceEqual([active]), "安徒恩四元组初始化各自9位剩余计数");
        var first = QuestRaidPhaseProgress.Apply([active], bound, catalog, 0);
        That(first[0].ConditionState == 4u + (5u << 9), "P0只减少P0通道一次，q2=5不能作事件增量");
        var second = QuestRaidPhaseProgress.Apply(first, bound, catalog, 1);
        That(second[0].ConditionState == 4u + (4u << 9), "P1只减少P1通道，不串改P0");
        var depleted = second;
        for (int index = 0; index < 10; index++)
        {
            depleted = QuestRaidPhaseProgress.Apply(depleted, bound, catalog, 0);
            depleted = QuestRaidPhaseProgress.Apply(depleted, bound, catalog, 1);
        }
        That(depleted[0].ConditionState == 0, "阶段计数归零后饱和，不向相邻通道借位");
        var replaced = active with { AcceptanceId = Guid.NewGuid() };
        That(QuestRaidPhaseProgress.Apply([replaced], bound, catalog, 0).SequenceEqual([replaced]), "旧阶段不能推进放弃后重新接取的同任务槽");
        var moved = active with { Slot = 4 };
        That(QuestRaidPhaseProgress.Apply([moved], bound, catalog, 0).SequenceEqual([moved]), "阶段绑定保留原持久槽身份");
        That(QuestRaidPhaseProgress.Apply([active], [], catalog, 0).SequenceEqual([active]) &&
            QuestRaidPhaseProgress.Apply([active], bound, catalog, 2).SequenceEqual([active]), "冻结后新接及非法阶段不产生任务事件");
        foreach (uint invalid in new[] { 6u + (5u << 9), 5u + (6u << 9), 1u << 18 })
            Reject<InvalidDataException>(() => QuestRaidPhaseProgress.Apply([active with { ConditionState = invalid }], bound, catalog, 0),
                "拒绝超出目标或占用未声明通道的存量scalar");
        foreach (var unsupported in new[]
        {
            quest with { Grade = "[title]" }, quest with { IntData = new[] { 0, 5, -1 } },
            quest with { IntData = new[] { 0, 1, 5, -1 } }, quest with { IntData = new[] { 0, -1, 5, -1 } },
            quest with { IntData = new[] { 0, 0, 512, -1 } }, quest with { IntData = new[] { 0, 0, 5, 1 } }
        }) That(!QuestRaidPhaseProgress.Supports(unsupported), "三整数、称号、其它攻坚或未证身份不套普通安徒恩目标");
        var triple = quest with { IntData = new[] { 0, 0, 1, -1, 1, 0, 511, 0, 0, 0, 7, -1 } };
        var tripleCatalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [quest.Id] = triple });
        var three = active with { ConditionState = QuestRaidPhaseProgress.Initial(triple) };
        var threeResult = QuestRaidPhaseProgress.Apply([three], [three], tripleCatalog, 0)[0];
        That(threeResult.ConditionState == (511u << 9) + (6u << 18), "三个独立通道按ordinal处理，同阶段多目标各推进一次");
        var point = new DfoServer.GameWorld.Towns.Location(1, 0, 1, 1);
        var character = new Character(1, 0, "AntonQuest", 8, point, point) { ActiveMainQuests = [active] };
        var progress = new AntonPhaseQuestProgress(8, 9, [active], first);
        var installed = GameSession.ApplyAntonPhaseQuest(character, progress);
        That(installed.Revision == 9 && installed.ActiveMainQuests.SequenceEqual(first) &&
            ReferenceEquals(installed, GameSession.ApplyAntonPhaseQuest(installed, progress)), "任务回执安装版本和实际任务，重复安装不再推进");
        var later = installed with { Revision = 10 };
        That(ReferenceEquals(later, GameSession.ApplyAntonPhaseQuest(later, progress)), "旧回执不能覆盖后续资产版本");
        Reject<InvalidOperationException>(() => GameSession.ApplyAntonPhaseQuest(character with { Revision = 7 }, progress),
            "不接受缺失原角色版本的任务安装");
        Console.WriteLine($"PASS {Count - checks} 安徒恩阶段任务通道、接取身份及快照安装断言");
    }

    internal static void RunResources(TcpTestResources resources, RaidScript script)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-anton-phase-quest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ownership = new AccountOwners(); var leases = new List<AccountOwners.Lease>();
        try
        {
            var quest = Quest(); var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [quest.Id] = quest });
            string path = Path.Combine(root, "game.db"); var repository = new CharacterRepository(path, catalog);
            var clock = new LukeRaidClockTests.Clock();
            var battle = new AntonRaidBattle(script, clock: clock, antonPhaseProgress: true,
                antonRewards: new(script, resources.ItemResources.Items, resources.ItemResources.Boosters));
            var actors = Enumerable.Range(0, 4).Select(index =>
            {
                var lease = ownership.Acquire("anton-phase-quest-" + index, () => { }); leases.Add(lease);
                var active = new ActiveMainQuest(3, (ushort)quest.Id, QuestRaidPhaseProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
                var character = repository.Create(lease, new NewCharacter("AntQuest" + index, resources.Towns.Room, resources.Towns.InitialReturn)
                    { ActiveMainQuests = index == 3 ? [] : [active] });
                var session = new GameSession(repository, resources.Towns, lease, questCatalog: catalog);
                session.Select(character.Slot); session.MarkReady();
                var actor = new RaidActor(session, lease, session.SelectionEpoch, character.Id, character.Name, new(2, 19));
                var binding = lease.Execute(() => session.PrepareRaidCombatStart(actor, battle));
                That(binding is not null, "任务集成夹具沿正式原actor绑定安徒恩Battle"); binding!.Install();
                Sql($"INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES({character.Id},'anton-phase-quest-{index}','{battle.Instance:N}',0,'{AntonResourceRewards.Source}',0,0)");
                return actor;
            }).ToArray();
            var originals = actors.Select(actor => actor.Session.Selected!).ToArray();
            DungeonClearFact? retainedClear = null;
            foreach (int dungeon in new[] { 210, 213, 215 }) Clear(dungeon);
            var candidate = battle.PendingAntonSettlement();
            That(candidate is { Members.Length: 4 }, "真实资源CLEAR PHASE冻结四个原成员与原任务绑定");
            Sql($"CREATE TRIGGER anton_phase_abort BEFORE UPDATE OF phase0_receipt ON character_raid_runs WHEN NEW.character_id={actors[1].Character} BEGIN SELECT RAISE(ABORT,'anton phase rollback'); END");
            Reject<SqliteException>(() => battle.CommitPendingAntonSettlement(repository), "末位成员阶段回执失败必须回滚全团任务和资格");
            for (int index = 0; index < actors.Length; index++)
            {
                var loaded = repository.LoadCharacter(actors[index].Owner, originals[index].Slot);
                That(loaded.Revision == originals[index].Revision && loaded.ActiveMainQuests.SequenceEqual(originals[index].ActiveMainQuests) &&
                    ReferenceEquals(actors[index].Session.Selected, originals[index]), "SQL失败后读库与在线快照均保持提交前状态");
            }
            That(Scalar("SELECT count(*) FROM character_raid_runs WHERE phase0_receipt IS NOT NULL") == 0 &&
                ReferenceEquals(battle.PendingAntonSettlement(), candidate), "末段失败不留下首成员半份资格且保留同候选重试");
            Sql("DROP TRIGGER anton_phase_abort");
            var lateAccepted = actors[3].Session.ChangeQuest((ushort)quest.Id, QuestLifecycleAction.Accept, catalog,
                resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            That(lateAccepted?.Outcome == QuestLifecycleOutcome.Committed, "CLEAR PHASE封存后允许本人正常接任务，不能用收窄接取掩盖竞态");
            var replacement = ownership.Acquire(actors[2].Owner.Account, () => { }); leases.Add(replacement);
            That(battle.CommitPendingAntonSettlement(repository) && battle.View.State == AntonRaidBattleState.Reward,
                "同候选重试成功后才发布Reward");
            for (int index = 0; index < 2; index++)
            {
                var loaded = repository.LoadCharacter(actors[index].Owner, originals[index].Slot);
                var online = actors[index].Session.Selected!;
                That(loaded.Revision == originals[index].Revision + 1 && online.Revision == loaded.Revision &&
                    loaded.ActiveMainQuests[0].ConditionState == 4u + (5u << 9) && online.ActiveMainQuests.SequenceEqual(loaded.ActiveMainQuests),
                    "阶段资格、各自任务与revision同事务提交并安装至原会话");
                That(actors[index].Session.RaidCombatResult(battle.View)?.RawFlag == 1,
                    "同事务任务回执完成后601才允许原生阶段事件");
                var ack = actors[index].Session.ObserveAntonPhaseQuestTrigger((ushort)quest.Id, new(0x10, 0, 0), catalog);
                That(ack?.Outcome == QuestLifecycleOutcome.Unchanged && ack.Character.Revision == loaded.Revision &&
                    actors[index].Session.ObserveAntonPhaseQuestTrigger((ushort)quest.Id, new(0x20, 0, 0), catalog) is null,
                    "原生33只回读当前阶段已减通道，其它阶段不能借用回执");
            }
            var disconnected = repository.LoadCharacter(replacement, originals[2].Slot);
            That(disconnected.Revision == originals[2].Revision && disconnected.ActiveMainQuests.SequenceEqual(originals[2].ActiveMainQuests) &&
                battle.AntonEligibility(actors[2].Character, 0)?.AntonQuestProgress is null,
                "原owner被接管后不借新owner更新任务，也不阻止其余原成员阶段提交");
            var lateOnline = actors[3].Session.Selected!;
            That(lateOnline.Revision == lateAccepted!.Character.Revision && lateOnline.ActiveMainQuests[0].ConditionState == 5u + (5u << 9) &&
                actors[3].Session.RaidCombatResult(battle.View)?.RawFlag == 0,
                "封存后新接不消费旧阶段任务，601不能让客户端本地误减新任务");
            var replay = repository.CommitAntonPhase(candidate!);
            That(replay.Take(2).All(row => row.AntonQuestProgress?.Revision ==
                    originals.Single(original => original.Id == row.Character).Revision + 1) &&
                !battle.CommitPendingAntonSettlement(repository) &&
                actors.Take(2).All(actor => repository.LoadCharacter(actor.Owner, 0).Revision == actor.Session.Selected!.Revision),
                "阶段回执重读保留原任务版本，重复clear不二次推进");
            var late = originals[0].ActiveMainQuests[0] with { AcceptanceId = Guid.NewGuid() };
            That(QuestRaidPhaseProgress.Apply([late], candidate!.Actors[0].Bindings, catalog, 0).SequenceEqual([late]),
                "封存候选保留原AcceptanceId，不能推进后续重新接取");
            VerifyProjectionAsync(actors[0], actors[1], battle, catalog, resources, retainedClear!).GetAwaiter().GetResult();

            void Clear(int dungeon)
            {
                string symbol = script.Phases[0].Dungeons.Single(row => row.Id == dungeon).HpSymbol;
                int count = Math.Max(1, battle.View.Symbols[symbol]);
                for (int index = 0; index < count; index++)
                {
                    var identity = new DungeonRunIdentity(actors[0].Character, 0, 1, Guid.NewGuid(), 1);
                    bool retain = dungeon == 215 && index == count - 1;
                    (RaidActor Actor, DungeonRunIdentity Identity)[] runActors = retain
                        ? [(actors[0], identity), (actors[1], identity with { CharacterId = actors[1].Character })]
                        : [(actors[0], identity)];
                    using var reserved = battle.ReserveDungeon(new(battle.Instance, 0, 1), (uint)dungeon, runActors);
                    That(reserved is not null, "阶段任务夹具使用当前Run预留消耗资源HP"); reserved!.Install();
                    var fact = new DungeonClearFact(identity, (uint)dungeon, [Guid.NewGuid()])
                        { ClearMap = new("boss", new(0, 0), 77, "fixture/anton-phase-quest.map") };
                    That(battle.CompleteAntonReservedDungeon(fact) is not null, "当前Run已提交清图事实推进真实资源阶段");
                    if (battle.PendingAntonSettlement() is not null) retainedClear = fact;
                    else battle.ReleaseDungeon(actors[0], identity.InstanceId);
                }
            }
            void Sql(string sql)
            {
                using var db = new SqliteGameDatabase(path).Open(); using var query = db.CreateCommand();
                query.CommandText = sql; query.ExecuteNonQuery();
            }
            long Scalar(string sql)
            {
                using var db = new SqliteGameDatabase(path).Open(); using var query = db.CreateCommand();
                query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar());
            }
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {Count - checks} 安徒恩阶段任务整团事务、失败回滚、原生33与重放断言");
    }
}
