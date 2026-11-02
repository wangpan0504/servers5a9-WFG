using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassFormalClearCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(8));
        var clock = new FormalClearClock(at);
        var definition = new DungeonDefinition(3, "dungeon/core-normal.dgn", null, null, [], []);
        var role = new Character(1, 0, "NormalCore", 0, Location, Location);
        var (run, authority) = CreateRun(role, entry => entry with
            { DungeonId = 3, QuestId = 0, ActiveQuestSlot = byte.MaxValue, RouteQuestId = 0, GuildMileageEligible = true });
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
        var pending = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
        pending.FreezeBattlePassFormalClear(definition, run.Entry, 0, clock);
        var source = pending.BattlePassFormalClear!;
        That(source.Recommended && source.NoReviveCoin && source.ReviveCoinsSpent == 0 &&
            source.Identity == run.Snapshot.Identity && source.PlannedAt == at && source.Matches(run, pending),
            "原权威末杀与同实例准备冻结推荐/整场零扣币来源，非客户端分数");
        clock.Now = at.AddMinutes(1);
        pending.FreezeBattlePassFormalClear(definition, run.Entry, 1, clock);
        That(ReferenceEquals(source, pending.BattlePassFormalClear) && source.NoReviveCoin && source.PlannedAt == at,
            "同immutable结算恢复不重冻原实际扣币事实或计划时间");
        var copied = new PreparedDungeonSettlement(run.ClearFact! with
            { Identity = run.Snapshot.Identity with { InstanceId = Guid.NewGuid() } }, [1, 2, 3], Reward, pending.MapId);
        That(!source.Matches(run, copied), "不同instance的复制结算不得改绑来源");
        foreach (var (id, category, target, xp, raw) in new[] {
            (3,0,2,100,0),(25,1,6,1000,0),(51,2,300,1600,0),(6,0,2,100,5),(37,1,6,1000,5) })
        {
            var mission = new BattlePassMission(id,target,xp,raw,"fixture-normal",-1);
            That(source.MatchesMission((byte)category,mission) && !source.MatchesMission((byte)((category+1)%3),mission) &&
                !source.MatchesMission((byte)category,mission with { RawParameter=1 }) &&
                !source.MatchesMission((byte)category,mission with { RequiredCount=target+1 }) &&
                !source.MatchesMission((byte)category,mission with { Experience=xp+1 }),
                "五原定义的类别/数量/XP/参数保持，不扩原生调度："+id);
        }
        var coinUsed = BattlePassFormalClear.Prepare(definition,run.Entry,run.ClearFact!,1,clock)!;
        That(coinUsed.Recommended && !coinUsed.NoReviveCoin && coinUsed.MatchesMission(2,new(51,300,1600,0,"",-1)) &&
            !coinUsed.MatchesMission(0,new(6,2,100,5,"",-1)), "实际用币仍可推荐通关，禁止无扣币计数");
        var nonRecommended = run.Entry with { GuildMileageEligible=false };
        var noCoin = BattlePassFormalClear.Prepare(definition,nonRecommended,run.ClearFact!,0,clock)!;
        That(!noCoin.Recommended && noCoin.NoReviveCoin && !noCoin.MatchesMission(2,new(51,300,1600,0,"",-1)) &&
            noCoin.MatchesMission(0,new(6,2,100,5,"",-1)), "非推荐零扣币只匹配无扣币任务");
        That(BattlePassFormalClear.Prepare(definition,nonRecommended,run.ClearFact!,1,clock) is null &&
            BattlePassFormalClear.Prepare(definition,run.Entry,run.ClearFact!,-1,clock) is null,
            "既非推荐且实际用币及非法负币事实不建来源");
        foreach(var excluded in new[]{run.Entry with { IsPractice=true },run.Entry with { IsTutorial=true },
            run.Entry with { IsQuestReplay=true },run.Entry with { DisableClearReward=true },
            run.Entry with { RaidEntry=new RaidDungeonIdentity(Guid.NewGuid(),1,0) },run.Entry with { RouteQuestId=3145 }})
            That(BattlePassFormalClear.Prepare(definition,excluded,run.ClearFact!,0,clock) is null,
                "原练习/教程/重放/禁奖/攻坚/剧情入口不借普通来源");
        var classExperience = run.Entry with { DungeonId=7142, Maze=run.Entry.Maze with { IsClassExperience=true } };
        That(classExperience.IsClassExperience && BattlePassFormalClear.Prepare(definition with { Id=7142 },
            classExperience,run.ClearFact! with { DungeonId=7142 },0,clock) is null,
            "原7142与classMaze共同组成真实体验分类，正常定义与事实同图号仍不能创建来源");
        That(BattlePassFormalClear.Prepare(definition with { Id=4 },run.Entry,run.ClearFact!,0,clock) is null &&
            BattlePassFormalClear.Prepare(definition,run.Entry,run.ClearFact! with { ConfirmedDeathEvents=[] },0,clock) is null,
            "不同正常DGN或没有原权威死亡事实拒绝");
        That(!source.MatchesMission(0,new(7,1,100,6,"",-1)) && !source.MatchesMission(0,new(22,2,100,22,"",1)),
            "未核S评分和黄金牌任务完全不接本来源");
        foreach (byte grade in new byte[] { 0, 10, 20, 30, 50, 60, 80, 90, 99 })
        {
            var ranked = BattlePassFormalClear.Prepare(definition, run.Entry, run.ClearFact!, 1, clock, grade, 80)!;
            bool expected = grade >= 80;
            That(ranked.FinalRankGrade == grade && ranked.MinimumSRankGrade == 80 && ranked.SRankOrAbove == expected &&
                ranked.MatchesMission(0, new(7, 1, 100, 6, "", -1)) == expected &&
                ranked.MatchesMission(1, new(26, 3, 1000, 6, "", -1)) == expected &&
                ranked.MatchesMission(2, new(51, 300, 1600, 0, "", -1)) &&
                !ranked.MatchesMission(0, new(6, 2, 100, 5, "", -1)),
                "原最终档冻结后只按原S档阈值匹配，推荐与实际扣币语义保持：" + grade);
        }
        var nonRecommendedRanked = BattlePassFormalClear.Prepare(definition, nonRecommended, run.ClearFact!, 1, clock, 80, 80)!;
        That(!nonRecommendedRanked.Recommended && !nonRecommendedRanked.NoReviveCoin && nonRecommendedRanked.SRankOrAbove &&
            nonRecommendedRanked.MatchesMission(0, new(7, 1, 100, 6, "", -1)) &&
            !nonRecommendedRanked.MatchesMission(0, new(3, 2, 100, 0, "", -1)) &&
            !nonRecommendedRanked.MatchesMission(0, new(6, 2, 100, 5, "", -1)),
            "非推荐且实际扣币的合法S档仅匹配原评分任务，不借旧推荐或零币门");
        That(BattlePassFormalClear.Prepare(definition, nonRecommended, run.ClearFact!, 1, clock, 60, 80) is null &&
            BattlePassFormalClear.Prepare(definition, run.Entry, run.ClearFact!, 0, clock, 80, 0) is null &&
            BattlePassFormalClear.Prepare(definition, run.Entry, run.ClearFact!, 0, clock, null, 80) is null,
            "无其它资格的低档及缺原阈值或缺原最终档不创建评分事实");
        foreach (var (id, category, required, xp) in new[] { (7, 0, 1, 100), (26, 1, 3, 1000) })
        {
            var mission = new BattlePassMission(id, required, xp, 6, "", -1);
            That(nonRecommendedRanked.MatchesMission((byte)category, mission) &&
                !nonRecommendedRanked.MatchesMission((byte)((category + 1) % 3), mission) &&
                !nonRecommendedRanked.MatchesMission((byte)category, mission with { RawParameter = 1 }) &&
                !nonRecommendedRanked.MatchesMission((byte)category, mission with { RequiredCount = required + 1 }) &&
                !nonRecommendedRanked.MatchesMission((byte)category, mission with { Experience = xp + 1 }),
                "原评分两定义完整类别/参数/数量/XP边界：" + id);
        }
        var (rankedRun, rankedAuthority) = CreateRun(role, entry => entry with
            { DungeonId = 3, QuestId = 0, ActiveQuestSlot = byte.MaxValue, RouteQuestId = 0, GuildMileageEligible = false });
        rankedRun.AcceptConfirmedDeath(rankedAuthority.Death(rankedRun.Snapshot.Identity, 20));
        var rankedPending = rankedRun.PrepareSettlement(rankedRun.ClearFact!, [4, 5, 6], Reward).Pending!;
        rankedPending.FreezeBattlePassFormalClear(definition, rankedRun.Entry, 1, clock, 80, 80);
        var frozenRank = rankedPending.BattlePassFormalClear!;
        rankedPending.FreezeBattlePassFormalClear(definition, rankedRun.Entry, 0, clock, 60, 80);
        That(ReferenceEquals(frozenRank, rankedPending.BattlePassFormalClear) && frozenRank.FinalRankGrade == 80 &&
            frozenRank.ReviveCoinsSpent == 1 && frozenRank.SRankOrAbove && frozenRank.Matches(rankedRun, rankedPending),
            "原同Pending失败或重复恢复不改原最终评分、扣币或来源身份");
        Console.WriteLine($"PASS BattlePass normal formal clear source core ({Count-before} checks)");
    }
    sealed class FormalClearClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; }=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
