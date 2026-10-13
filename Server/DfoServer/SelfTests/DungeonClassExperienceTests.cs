using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonClassExperienceTests
{
    public static void RunCore()
    {
        int before = Count;
        var maze = new DungeonMaze(0,1,1,"AA",[new(0,0)],[new(0,0)],null,[]);
        DungeonRawSection[] markers = [.. new[] { "disable drop item", "disable clear reward", "no fatigue",
            "no experience monster and result", "no giveup panalty" }.Select(tag => new DungeonRawSection(tag,[])),
            new("enter without fatigue",[new(0,1)])];
        var definition = new DungeonDefinition(7142,"dungeon/cataclysm/changeclass/7142_changeclass.dgn",1,15,[maze],markers);
        const string path = "map/cataclysm/changeclass/76032_changeclass_s_(0,0).map";
        var pool = new DungeonMapPool([new(76032,path,[7142],"[normal]",1)]);
        var resolved = pool.Resolve(definition,maze,_ => throw new InvalidOperationException("单房间不抽选"));
        That(resolved.IsClassExperience && resolved.Greed == "AA" && resolved.StaticTopology is { Supported: true, Links.Count: 0 } &&
            resolved.MapSpecification is [{ Id: 76032, Kind: "boss" }], "体验AA为有地图无出口的独立单房，不改写Greed");
        foreach (var marker in markers)
            That(!(definition with { RawSections = markers.Where(row => row != marker).ToArray() }).IsClassExperience,
                "体验资源缺失免费或禁奖励声明不能放宽普通入场");
        That((definition with { RawSections = [.. markers, markers[0]] }).IsClassExperience &&
            !(definition with { RawSections = [.. markers, new("no fatigue",[new(0,1)])] }).IsClassExperience,
            "重复空标记保留，冲突标记拒绝");
        That(ReferenceEquals(pool.Resolve(definition with { Id = 7143 },maze,_ => 0),maze) &&
            ReferenceEquals(new DungeonMapPool([new(76032,path,[7143],"[normal]",1)]).Resolve(definition,maze,_ => 0),maze),
            "普通空格和错误MAP归属不借体验例外补图");
        That(ReferenceEquals(new DungeonMapPool([new(76032,path,[7142],"[normal]",1),new(76033,path,[7142],"[normal]",1)])
            .Resolve(definition,maze,_ => 0),maze), "体验MAP身份歧义明确拒绝，不随机换图");
        var character = new Character(17,0,"Experience",0,new Location(38,1,450,234),null)
            { Level = 16, Job = 3, ActiveMainQuests = [new(0,3145,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(definition,character,7142,0,0,out var entry,out _,resolveMaze: _ => resolved) &&
            entry is { IsClassExperience: true, UsesFatigue: false, DisableClearReward: true, QuestAcceptances.IsEmpty: true,
                InitializationCounterSupported: false }, "体验计划冻结免费/禁奖励策略，不绑定正式任务或初始化计数");
        That(!DungeonEntryPlan.TryCreate(definition,character,7142,3145,0,out _,out _,resolveMaze: _ => resolved) &&
            !DungeonEntryPlan.TryCreate(definition,character,7142,0,1,out _,out _,resolveMaze: _ => resolved),
            "体验模式不能携带正式任务或非零难度");
        var room = new DungeonRoomDefinition(entry!.StartMap,[new(75046,[75046,1,0,-99,-258,10000,1,1],"[fixed]","[boss]","monster/trial.mob")],[],[]);
        var monsters = DungeonMonsterPlan.FromRoom(room,character.Id,256,15,playerTeam: entry.PlayerTeam);
        That(entry.PlayerTeam == 0 && monsters.PlayerTeam == 0 &&
            monsters.Monsters is [{ Team: 100, IsFriendly: false, IsBlocking: true }],
            "体验复用单人team0上下文，真实Boss缺省team100保持敌方资格，不因新阵营接口丢失入场身份");
        var authority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,1,new([monsters],[]),authority,out var run,out _) && authority.BindTo(run!),
            "体验复用正式身份、资源演员与原生战斗权威");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        var reported = authority.ValidateAndIssue(run,run.Snapshot.Identity,
            new(256,17,0,17,100,1,0,15,100,0,0,true,new string('A',64)));
        That(reported.Death is not null, "体验死亡仍须通过原生目标与统计核验");
        var prepared = run.PrepareConfirmedDeath(reported.Death!).Pending!;
        var result = run.ConfirmCommittedDeath(prepared);
        That(result.Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon && run.ClearFact is null &&
            run.Snapshot.LivingMonsters.Count == 0 && run.Snapshot.ConsumedFatigue == 0,
            "体验Boss死亡只退休内存演员，不生成通关资格或普通疲劳");
        That(run.ConfirmCommittedDeath(prepared).Outcome == DungeonRunOutcome.Duplicate &&
            run.Return(run.Snapshot.Identity,Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
            "体验死亡重放不重复推进，明确退出结束原实例");
        Console.WriteLine($"PASS {Count-before} 职业体验资源、单房拓扑与无结算生命周期断言");
    }
}
