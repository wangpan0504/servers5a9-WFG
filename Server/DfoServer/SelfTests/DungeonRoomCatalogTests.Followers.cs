using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonRoomCatalogTests
{
    internal static void RunMonsterFollowers()
    {
        var fixture = new Fixture();
        var row = fixture.Monster()[1..^1];
        var monsters = fixture.Block("monster", [.. row, .. row]);
        var follow = fixture.Block("monster follow type", [fixture.String("none"),fixture.String("follow")]);
        var parsed = fixture.Parse([.. monsters, .. follow]);
        That(parsed.Monsters.Select(actor => actor.FollowType).SequenceEqual(["none","follow"]),
            "monster跟随声明按资源行冻结，不因相同模板合并身份");
        follow[2] = fixture.String("none");
        That(parsed.Monsters[1].FollowType == "follow", "跟随声明不引用可变输入");
        var invalidString = fixture.String("invalid-string-pool-reference");
        var invalidFollow = fixture.Block("monster follow type", [fixture.String("follow"),invalidString]);
        var invalidReferences = DungeonRoomCatalog.ParseResource(parsed.Map,[.. monsters, .. invalidFollow],
            id => id == invalidString.Value ? throw new InvalidDataException("字符串池引用越界") : fixture.Resolve(id),
            new Dictionary<int,string> { [1]="monster/fixture.mob" },new Dictionary<int,string>(),_ => true);
        That(invalidReferences.Monsters.All(actor => actor.FollowType is null),
            "可选follow字符串池引用损坏整块失效，不拒绝旧地图或留下前一行资格");
        foreach (PvfToken[] optional in new PvfToken[][]
        {
            Array.Empty<PvfToken>(), fixture.Block("monster follow type", [fixture.String("follow")]),
            fixture.Block("monster follow type", [new(0,1),fixture.String("follow")]),
            fixture.Block("monster follow type", [fixture.String("none"),fixture.String("follow"),fixture.String("follow")]),
            new[] { fixture.Tag("monster follow type"),fixture.String("none"),fixture.String("follow") },
            [.. follow, .. follow]
        })
            That(fixture.Parse([.. monsters, .. optional]).Monsters.All(actor => actor.FollowType is null),
                "缺省、错型、错位、未闭合及重复跟随声明保留旧地图，但不授跨房资格");

        var firstMap = parsed.Map;
        var middleMap = firstMap with { Id=2,Position=new(1,0) };
        var bossMap = firstMap with { Id=3,Position=new(2,0),Kind="boss" };
        var firstRoom = parsed with { Monsters = parsed.Monsters.Select(actor => actor with { Team=0 }).ToArray() };
        var first = DungeonMonsterPlan.FromRoom(firstRoom,99,10,playerTeam:0);
        var middle = DungeonMonsterPlan.FromRoom(new(middleMap,[parsed.Monsters[1] with { Team=0 }],[],[]),99,20,playerTeam:0);
        var boss = DungeonMonsterPlan.FromRoom(new(bossMap,[parsed.Monsters[1] with { Team=100,RankKind="[boss]" }],[],[]),99,30,playerTeam:0);
        var maze = new DungeonMaze(0,3,1,"fixture-monster-follow",[firstMap.Position],[bossMap.Position],
            [firstMap,middleMap,bossMap],[]);
        var entry = new DungeonEntryPlan(99,17,3,0,byte.MaxValue,0,maze,firstMap,bossMap) { PlayerTeam=0 };
        var route = new DungeonFixedRoute([first,middle,boss],
            [new(firstMap.Position,middleMap.Position),new(middleMap.Position,bossMap.Position)]);
        var authority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,authority,out var created,out _) && authority.BindTo(created!),
            "友方monster跟随不改变路线或战斗权威装配");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var oldIdentity = run.Snapshot.Identity;
        That(run.PublishedFriendlyActor(10) is null && run.PublishedFriendlyActor(11) == first.Monsters[1] &&
            run.PublishedFriendlyActor(20) is null && run.PublishedFriendlyActor(30) is null && run.PublishedFriendlyActor(999) is null,
            "只认已加载友方follow；非follow、未来房、敌方和未知key不获跨房资格");
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),middleMap.Position).Outcome == DungeonRunOutcome.Accepted,
            "友方随行不阻挡正常换房");
        var observation = new NativeDungeonScriptedDeathObservation(11,0x22,true,new string('A',64));
        That(authority.ValidateAndIssueScripted(run,run.Snapshot.Identity,observation).Outcome == DungeonRunOutcome.Rejected,
            "跨房follow不绕过loading门禁");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.PublishedFriendlyActor(11) == first.Monsters[1] && run.PublishedFriendlyActor(20) == middle.Monsters[0] &&
            authority.ValidateAndIssueScripted(run,oldIdentity,observation).Outcome == DungeonRunOutcome.Rejected &&
            authority.ValidateAndIssueScripted(run,run.Snapshot.Identity,observation with { ChecksumMatches=false }).Outcome == DungeonRunOutcome.Rejected,
            "已加载随行保持原key，仍拒绝旧epoch和错误校验");
        run.RetireNonCombatObject(10);
        run.RetireNonCombatObject(999);
        run.RetireNonCombatObject(30);
        That(authority.ValidateAndIssueScripted(run,run.Snapshot.Identity,observation) is { Outcome:DungeonRunOutcome.Accepted,Death:null },
            "友方follow退场不签发战斗死亡事实");
        run.RetireNonCombatObject(11);
        run.RetireNonCombatObject(11);
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),firstMap.Position).Outcome == DungeonRunOutcome.Accepted,
            "退场后可沿原路线回访首房");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] {10}) && run.ClearFact is null,
            "重放不复活follow；未跟随友方不被跨房误退场，也不制造清图");
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),middleMap.Position);
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),bossMap.Position);
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        run.RetireNonCombatObject(30);
        That(run.PublishedFriendlyActor(30) is null && run.Snapshot.LivingMonsters.SequenceEqual(new ushort[] {30}),
            "已加载的敌方follow仍走真实战斗死亡，不被友方退场路径清除");
        var nextAuthority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,2,200,route,nextAuthority,out var next,out _) && nextAuthority.BindTo(next!),
            "新实例独立建立随行状态");
        next!.CompleteLoading(next.Snapshot.Identity,Guid.NewGuid());
        That(next.Snapshot.LivingMonsters.Count == 2 &&
            nextAuthority.ValidateAndIssueScripted(next,run.Snapshot.Identity,observation).Outcome == DungeonRunOutcome.Rejected,
            "旧实例身份不能退场新演员，新实例不继承旧墓碑");
    }
}
