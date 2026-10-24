using DfoServer.Game.Dungeons;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Testing;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using System.Collections.Immutable;
using PvfLib;
using DfoServer.Game.Inventory.Models;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonSpecialEntryTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills,
        CharacterGrowthCatalog growth, QuestCatalog quests)
    {
        int beforeCount = Count;
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 70);
        var repo = fixture.Repository;
        var owner = fixture.Owner;
        var before = fixture.Read();
        before = repo.CommitInventory(owner, before, CurrencyService.SetGold(before, 100000).Plan!);
        const int day = 739911;
        var prices = new DungeonAltarAdmissionCatalog(10000, 1200, [new(1, 99, 12000)], [new(1, 0), new(2, 60), new(3, 200)]);
        var definition = new DungeonDefinition(11006, "synthetic", 60, 60, [],
            [new("blood dungeon", [new(0, 1)]), new("no fatigue", []), new("enter without fatigue", [new(0, 1)])])
            { DisableFatigueConsumption = true };
        That(DungeonAltarEntryPolicy.Prepare(before, definition, day, new(0, 0), prices)?.Gold == 22000 &&
            DungeonAltarEntryPolicy.Prepare(before, definition, day, new(1, 1), prices)?.Gold == 29200 &&
            DungeonAltarEntryPolicy.Prepare(before, definition, day, new(2, 1), prices)?.Gold == 46000 &&
            DungeonAltarEntryPolicy.Prepare(before, definition, day, new(3, 1), prices) is null,
            "无尽三次兼容规则分别使用本次收费，第四次拒绝");
        That(DungeonAltarEntryPolicy.Prepare(before with { Level = 59 }, definition, day, new(0, 0), prices) is null &&
            DungeonAltarEntryPolicy.Prepare(before with { Inventory = before.Inventory with { Gold = 21999 } }, definition, day, new(0, 0), prices) is null,
            "等级与金币不足时没有入场准备");
        var usage = repo.LoadSpecialEntryUsage(owner, before, day, DungeonSpecialEntryKind.Blood);
        That(usage == new DungeonSpecialEntryUsage(0, 0), "新角色专用入场账本为空");
        var identity = new DungeonRunIdentity(before.Id, 0, before.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1);
        var spent = CurrencyService.Spend(before.Inventory, CurrencyKind.Gold, 22000).Plan!;
        var charge = new DungeonEntryCharge(spent, 0, identity, 16350)
        {
            DungeonId = 11006,
            Special = new(day, DungeonSpecialEntryKind.Blood, 0, 22000, usage)
        };
        fixture.Sql("CREATE TRIGGER fail_special_entry BEFORE INSERT ON dungeon_special_entry_receipts BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        try
        {
            TestCheck.Reject<SqliteException>(() => repo.CommitDungeonEntry(owner, before, 500, charge), "入场账本插入失败触发事务回滚");
            var afterFailure = fixture.Read();
            That(afterFailure.Revision == before.Revision && afterFailure.Inventory.Gold == before.Inventory.Gold &&
                afterFailure.DungeonState == before.DungeonState && repo.LoadSpecialEntryUsage(owner, before, day, DungeonSpecialEntryKind.Blood) == usage,
                "账本失败不留下扣费、次数、key或尝试号的一半提交");
        }
        finally { fixture.Sql("DROP TRIGGER fail_special_entry"); }
        var committed = repo.CommitDungeonEntry(owner, before, 500, charge);
        That(committed.Inventory.Gold == 78000 && committed.DungeonState!.NextObjectKey == 500 &&
            committed.DungeonState.LastAttempt == identity.TaskAttempt &&
            repo.LoadSpecialEntryUsage(owner, committed, day, DungeonSpecialEntryKind.Blood) == new DungeonSpecialEntryUsage(1, 1),
            "扣费、原尝试号和专用入场事实同时提交");
        TestCheck.Reject<InvalidDataException>(() => repo.CommitDungeonEntry(owner, committed, 501, charge), "重复尝试号不能重复扣费");
        That(fixture.Read().Inventory.Gold == committed.Inventory.Gold, "重复请求保留原金币");
        var next = charge with
        {
            Identity = identity with { InstanceId = Guid.NewGuid(), TaskAttempt = identity.TaskAttempt + 1 },
            Items = CurrencyService.Spend(committed.Inventory, CurrencyKind.Gold, 22000).Plan
        };
        TestCheck.Reject<InvalidOperationException>(() => repo.CommitDungeonEntry(owner, committed, 501, next), "旧计数准备不能覆盖最新入场");
        That(fixture.Read().Inventory.Gold == committed.Inventory.Gold && fixture.Read().DungeonState == committed.DungeonState,
            "计数冲突也回滚费用和key");
        next = next with { Special = charge.Special! with { Expected = new(1, 1) } };
        committed = repo.CommitDungeonEntry(owner, committed, 501, next);
        That(repo.LoadSpecialEntryUsage(owner, committed, day, DungeonSpecialEntryKind.Blood) == new DungeonSpecialEntryUsage(2, 1) &&
            repo.LoadSpecialEntryUsage(owner, committed, day + 1, DungeonSpecialEntryKind.Blood) == new DungeonSpecialEntryUsage(0, 0) &&
            repo.LoadSpecialEntryUsage(owner, committed, day, DungeonSpecialEntryKind.Despair) == new DungeonSpecialEntryUsage(0, 0),
            "同角色重入只增加次数，日期和玩法计数隔离");
        var authorities = new Dictionary<SinglePlayerDungeonRun, NativeDungeonCombatAuthority>();
        var run = NewUltimate();
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var state = run.CurrentAltar!;
        for (int round = 0; round < 7; round++)
        {
            if (round == 0 || state.WaitingForDifficulty) state.Begin(round * 1000, 1);
            while (state.NextDue is { } due)
            foreach (var actor in state.TakeDue(due)!.Actors)
            {
                var authority = authorities[run];
                var death = authority.ValidateAndIssueAltar(run, run.Snapshot.Identity, new(actor.ObjectKey, new string('A',64)));
                var pending = run.PrepareConfirmedDeath(death.Death!);
                run.ConfirmCommittedDeath(pending.Pending!);
            }
            run.AdvanceAltar(round * 1000 + 1);
        }
        var reward = new DungeonAltarRewardPlan(7, 7, 123,
            Enumerable.Repeat(new DungeonAltarRewardDraw(100, null), 10).ToImmutableArray(), 123456);
        var inventory = new InventoryPlan(committed.Inventory, committed.Inventory with { Gold = committed.Inventory.Gold + 1000 }, "altar_clear_reward");
        var frozen = run.PrepareAltarSettlement(committed, reward, inventory, 7000)!;
        That(frozen is { Fact.Cleared: true } && frozen.Fact.DeathEvents.Length == 7 && run.HasPendingSettlement,
            "极限通关结算只冻结七回合真实死亡和原实例奖励");
        fixture.Sql("CREATE TRIGGER fail_altar_clear BEFORE INSERT ON dungeon_altar_clear_receipts BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        try
        {
            TestCheck.Reject<SqliteException>(() => repo.CommitDungeonAltarSettlement(owner, run, frozen, growth), "祭坛回执失败触发整笔结算回滚");
            var rolledBack = fixture.Read();
            That(rolledBack.Revision == committed.Revision && rolledBack.Inventory.Gold == committed.Inventory.Gold &&
                rolledBack.Learning.Experience == committed.Learning.Experience && run.IsPendingAltarSettlement(frozen),
                "失败结算不留下金币、经验、revision或内存确认的一半结果");
        }
        finally { fixture.Sql("DROP TRIGGER fail_altar_clear"); }
        var settled = repo.CommitDungeonAltarSettlement(owner, run, frozen, growth);
        That(!settled.Duplicate && settled.Character.Inventory.Gold == committed.Inventory.Gold + 1000 &&
            settled.Character.Learning.Experience == committed.Learning.Experience + 123 && settled.BestRounds == 7 && settled.BestMilliseconds == 7000,
            "祭坛金币、经验与历史记录共事务到账");
        var duplicate = repo.CommitDungeonAltarSettlement(owner, run, frozen, growth);
        That(duplicate.Duplicate && duplicate.Character.Revision == settled.Character.Revision &&
            duplicate.Character.Inventory.Gold == settled.Character.Inventory.Gold && duplicate.GainedExperience == settled.GainedExperience,
            "提交成功但响应丢失时按冻结回执恢复，不二次发奖");
        That(run.ConfirmCommittedAltarSettlement(frozen) && !run.HasPendingSettlement && run.HasCommittedAltarSettlement,
            "仅持久成功后释放本实例结算准备");
        committed = settled.Character;
        var failed = NewUltimate();
        failed.CompleteLoading(failed.Snapshot.Identity, Guid.NewGuid());
        var empty = new DungeonAltarRewardPlan(0, 7, 0, [new(0, null)], 123457);
        var emptyInventory = new InventoryPlan(committed.Inventory, committed.Inventory, "altar_clear_reward");
        var failure = failed.PrepareAltarSettlement(committed, empty, emptyInventory, 1, failed: true)!;
        That(failure.Fact is { Cleared: false, CompletedRounds: 0, DeathEvents.Length: 0 } && failed.ClearFact is null &&
            ReferenceEquals(failed.PrepareAltarSettlement(committed, empty, emptyInventory, 1, failed: true), failure),
            "零回合失败沿专用事实重试，不制造普通通关事实");
        var failureCommit = repo.CommitDungeonAltarSettlement(owner, failed, failure, growth);
        That(failureCommit.GainedExperience == 0 && failureCommit.Character.Inventory.Gold == committed.Inventory.Gold &&
            failureCommit.BestRounds == 7 && failureCommit.BestMilliseconds == 7000, "失败不覆盖更好历史，也不发放虚构奖励");
        Console.WriteLine($"PASS {Count - beforeCount} 塔与祭坛入场原子性、重复请求及计数隔离断言");

        SinglePlayerDungeonRun NewUltimate()
        {
            var map = new DungeonMap("boss", new(0,0), 17030, "map/fixture.map");
            var source = new PvfMapAltarWaves(true, [new([1001,0,1,1,0,2000,1000,1,0,1])],
                Enumerable.Range(0,7).Select(round => new PvfAltarPhase(round,0,1,1f,0,0,1)).ToImmutableArray());
            var room = new DungeonRoomDefinition(map, [], [], []) { AltarWaves = source };
            var actors = DungeonMonsterPlan.FromAltarRoom(room, committed.Id, checked((ushort)committed.DungeonState!.NextObjectKey), 70, _ => 0);
            var maze = new DungeonMaze(0,1,1,"BB",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(committed.Id, committed.Revision + 1, 11007,0,byte.MaxValue,0,maze,map,map) { BasisLevel = 70 };
            var authority = new NativeDungeonCombatAuthority();
            if (!SinglePlayerDungeonRun.TryCreate(entry, committed.DungeonState.LastAttempt + 1,1,new([actors],[]),authority,out var created,out var reason) || !authority.BindTo(created!))
                throw new InvalidOperationException(reason);
            authorities.Add(created!, authority);
            var currentUsage = repo.LoadSpecialEntryUsage(owner, committed, day, DungeonSpecialEntryKind.Ultimate);
            committed = repo.CommitDungeonEntry(owner, committed, actors.NextObjectKey, new(null,0,created!.Snapshot.Identity,map.Id)
                { DungeonId = 11007, Special = new(day,DungeonSpecialEntryKind.Ultimate,0,0,currentUsage) });
            return created;
        }
    }
}
