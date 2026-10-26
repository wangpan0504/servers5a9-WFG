using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyTagEvildomLife()
    {
        var participants = TagEvildomRunTests.Participants();
        var start = new DungeonMap("map", new(0, 0), 1, "map/tag-fixture-start.map");
        var boss = new DungeonMap("boss", new(1, 0), 2, "map/tag-fixture-boss.map");
        var maze = new DungeonMaze(0, 2, 1, "tag-fixture", [start.Position], [boss.Position], [start, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 9608, 0, byte.MaxValue, 0, maze, start, boss)
        {
            DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, DisableGiveupPenalty = true
        };
        var authority = new FixtureCombatAuthority();
        var route = new DungeonFixedRoute([Plan(start), BossPlan(boss, 10)], [new(start.Position, boss.Position)]);
        var run = Create(entry);
        var loading = run.Snapshot.Identity;
        That(run.TagEvildomLife is null && run.SwitchTagEvildom(loading, 1) is null && run.CurrentTagEvildomRevival() is null,
            "普通Run不凭两个角色或opcode自动成为TAG实例");
        That(run.InstallTagEvildom(participants) && !run.InstallTagEvildom(participants), "仅在批准Run初始加载中安装一次固定双槽");
        Reject<InvalidOperationException>(() => run.BindPartyInstance(Guid.NewGuid()), "已经绑定TAG生命状态的实例不能被普通组队改写Guid");
        That(run.SwitchTagEvildom(loading, 1) is null && run.ReportTagEvildomDeath(loading, 0, 100) is null,
            "加载未确认不能切角或提交玩家死亡");
        run.CompleteLoading(loading, Guid.NewGuid());
        var firstRoom = run.Snapshot.Identity;
        var death = run.ReportTagEvildomDeath(firstRoom, 0, 1_000)!;
        var due = run.CurrentTagEvildomRevival()!;
        That(death.SwitchTo == 1 && !death.DefeatAcknowledgement && run.TagEvildomLife!.ActiveOrdinal == 1 &&
            run.Entry.CharacterId == participants.Primary.Id && run.Snapshot.Identity.CharacterId == participants.Primary.Id,
            "死亡与TAG切活槽不改正式Run登录主角身份");
        That(run.SwitchTagEvildom(firstRoom with { InstanceId = Guid.NewGuid() }, 1) is null &&
            run.ReportTagEvildomDeath(firstRoom with { Epoch = firstRoom.Epoch + 1 }, 1, 2_000) is null,
            "旧实例或不匹配epoch不能改变两槽状态");
        That(run.Move(firstRoom, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted,
            "TAG生命事实沿原Run移动，不新建平行场景owner");
        That(run.CurrentTagEvildomRevival() is null && run.ReviveTagEvildom(due, 20_000) is null &&
            run.ReportTagEvildomDeath(run.Snapshot.Identity, 1, 20_000) is null,
            "房间加载中停止到期发布及死亡受理，不提前复活未加载Actor");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.SwitchTagEvildom(firstRoom, 0) is null && run.CurrentTagEvildomRevival() == due &&
            run.ReviveTagEvildom(due, 20_000)?.Revived == 0, "新房确认后仅原实例未取消的到期可复活，旧房请求仍无效");
        var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
        That(pending.Pending is not null && run.SwitchTagEvildom(run.Snapshot.Identity, 1) is null &&
            run.ReportTagEvildomDeath(run.Snapshot.Identity, 1, 21_000) is null,
            "待提交怪物死亡/结算仍沿正式Run互斥，不用TAG绕过资产边界");
        That(pending.Pending!.Result.ClearFact is null && pending.Pending.Result.RoomClearFact is null &&
            run.ConfirmCommittedDeath(pending.Pending).ClearFact is null && run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Dungeon &&
            run.Snapshot.LivingMonsters.Count == 0, "TAG真实Boss死亡可确认，但不生成普通通关/任务事实或进入普通翻牌结算");
        var fakeClear = new DungeonClearFact(run.Snapshot.Identity, 9608, []);
        var zeroExperience = new DungeonClearExperienceReward(0, 0, 0, -1, "fixture-clear", "fixture/tag.tbl");
        var ordinaryCard = new DungeonCardReward(100, null, "fixture-card", "fixture/tag.tbl", 1_000);
        That(run.PrepareSettlement(fakeClear, [1], zeroExperience, ordinaryCard).Outcome == DungeonRunOutcome.Rejected &&
            !run.HasPendingSettlement && run.PreparePassiveClear(run.Snapshot.Identity, Guid.NewGuid(), 1, 0) is null,
            "TAG明确拒绝普通零经验加金币翻牌准备和物件通关，不以no-exp借用奖励入口");
        run.ReportTagEvildomDeath(run.Snapshot.Identity, 0, 22_000);
        run.ReportTagEvildomDeath(run.Snapshot.Identity, 1, 22_001);
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Rejected &&
            run.Return(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "双死败北不能继续过房，但仍允许已证42回城终态");
        // 回城取消独立使用未进入结算的实例；测试不能伪造或跳过待提交事务。
        var leaving = Create(entry);
        leaving.InstallTagEvildom(participants);
        leaving.CompleteLoading(leaving.Snapshot.Identity, Guid.NewGuid());
        leaving.ReportTagEvildomDeath(leaving.Snapshot.Identity, 1, 1_000);
        var staleDue = leaving.CurrentTagEvildomRevival()!;
        That(leaving.Return(leaving.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            leaving.TagEvildomLife is { Cancelled: true } && leaving.CurrentTagEvildomRevival() is null &&
            leaving.ReviveTagEvildom(staleDue, 99_000) is null, "真实Run回城取消双槽期限，迟到任务不再发布");
        foreach (var wrong in new[] { entry with { DungeonId = 3 }, entry with { CharacterId = 98 },
            entry with { CharacterRevision = 18 }, entry with { DisableFatigueConsumption = false },
            entry with { DisableMonsterAndClearExperience = false }, entry with { DisableGiveupPenalty = false },
            entry with { IsPractice = true } })
            That(!Create(wrong).InstallTagEvildom(participants), "非目标图、错主角或缺资源免耗声明不能借用TAG状态");
        var tooLate = Create(entry);
        tooLate.CompleteLoading(tooLate.Snapshot.Identity, Guid.NewGuid());
        That(!tooLate.InstallTagEvildom(participants), "不能给已开战普通Run事后补一个TAG身份");
        That(tooLate.Move(tooLate.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            tooLate.Phase == SinglePlayerDungeonPhase.Loading && !tooLate.InstallTagEvildom(participants),
            "过房重新Loading不是初始加载，仍不能事后安装TAG身份");

        SinglePlayerDungeonRun Create(DungeonEntryPlan planned)
        {
            // 这里只复用既有Run的合成批准路线夹具；生产EntryPlan的再战准入guard保持不变。
            That(SinglePlayerDungeonRun.TryCreate(planned, 1, 100, route, authority, out var result, out _), "构造合成双槽Run生命周期夹具");
            return result!;
        }
    }
}
