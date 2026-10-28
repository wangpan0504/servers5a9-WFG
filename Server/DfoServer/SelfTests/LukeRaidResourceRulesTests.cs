using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实40条原资源的纯领域解释器回归。Run身份/事件由本夹具构造，不冒充Runtime、资产事务或原生对象/effect消费者。
internal static class LukeRaidResourceRulesTests
{
    internal static void Run(RaidScript script,RaidActor[] actors,LukeRaidCompletionPolicy completion)
    {
        RunRoute(script, actors, completion, 1);
        RunRoute(script, actors, completion, 2);
        EnergyBranches(script, actors);
    }

    static void RunRoute(RaidScript script, RaidActor[] actors, LukeRaidCompletionPolicy completion, int route)
    {
        int before=Count;var characters=actors.Select(actor=>actor.Session.Selected).ToArray();
        That(script.Phases.Sum(phase=>phase.Rules.Length)==40&&script.Issues.IsEmpty,"直接消费当前完整40规则，不删除危险规则伪称原资源");
        var battle=new AntonRaidBattle(script,kind:RaidKind.Luke,completion:completion);
        foreach(var actor in actors)That(battle.JoinStart(actor),"原始角色身份登记到纯解释器");
        foreach(int dungeon in route == 1 ? new[] {5054,5055,5056} : new[] {5054,5056,5055})
            for(int run=0;run<3;run++)
            {
                var rows=Enter(dungeon);
                if(dungeon==5054&&run==0)That(battle.View.Dungeons[5055]==RaidDungeonState.Open&&battle.View.Dungeons[5056]==RaidDungeonState.Open,
                    "完整原规则由Enter5054开放另两圣所");
                Clear(dungeon,rows);
                if(dungeon==(route==1?5055:5056)&&run==2)That(battle.View.Symbols["[2phase dungeon type]"]==route,"原目标HP归零后才选择route，非首次Run就选路");
                if(dungeon!=(route==1?5056:5055)||run!=2)That(!battle.HasPendingTransition,"原P1未完成三图各三次不能终结");
                Exit(rows);
            }
        That(battle.HasPendingTransition&&battle.PendingLukeSettlement()?.Members.Take(4).All(member=>member.CommittedRuns==9)==true,
            "原P1九Run才形成成功终结，参与按Run而非角色数累计");
        CommitDomainOnly();
        That(battle.View.Phase==1&&battle.View.Dungeons[5311]==RaidDungeonState.Open&&battle.View.Dungeons[5313]==RaidDungeonState.Open&&
            battle.View.Timers[new(1,5054)]==10&&battle.View.Symbols["[2phase dungeon type]"]==route,
            "原P2初始化、实例timer键和P1route继承，不篡改跨阶段5054键");
        foreach(int dungeon in new[]{5311,5313,5057,5058,5059,5060,5064,5065})
        {
            var rows=Enter(dungeon);Clear(dungeon,rows);Exit(rows);
            if(dungeon==5313)That(battle.View.Symbols["[altar enterance clear]"]==2&&battle.View.Dungeons[5063]==RaidDungeonState.Open,
                "原入口加法累计到2开祭坛及能源");
            if(dungeon==5060)That(battle.View.Symbols["[altar clear]"]==2&&battle.View.Dungeons[5063]==RaidDungeonState.Hide&&
                !battle.View.Timers.ContainsKey(new(1,5063)),"原祭坛计数、关能源及RESET缺键幂等执行");
        }
        That(battle.View.Symbols["[temple clear]"]==2&&battle.View.Dungeons[5061]==RaidDungeonState.Open,"原双圣殿计数打开王座路线");
        var throne=Enter(5061);
        battle.ObserveLukeRun(throne[0].Identity.InstanceId,throne,55845);
        That(battle.View.Dungeons[5066]==RaidDungeonState.Open&&battle.View.Timers[new(1,5066)]==150,
            "原地图55845事实开启能源储藏室与150原值timer，不等价已应用怪物effect");
        var energy=Enter(5066,2);Clear(5066,energy);Exit(energy);
        var timer=new RaidTimer(2,5066);long generation=battle.View.TimerGenerations[timer];
        That(battle.View.Dungeons[5066]==RaidDungeonState.Close&&battle.View.Timers[timer]==120&&!battle.View.Timers.ContainsKey(new(1,5066)),
            "原储藏室清图取消攻击timer并进入恢复120");
        That(battle.ExpireLukeTimer(battle.Instance,timer,generation) is not null&&battle.View.Dungeons[5066]==RaidDungeonState.Open&&
            battle.ExpireLukeTimer(battle.Instance,timer,generation) is null,"同timer代只到期一次，恢复原Open");
        Clear(5061,throne);Exit(throne);
        That(battle.View.Dungeons[5062]==RaidDungeonState.Open&&battle.View.Dungeons[5066]==RaidDungeonState.Hide&&
            battle.View.Symbols["[kill all character]"]==1,"原王座转换保留KILL符号事实，但本纯解释器不伪造死亡/扣币或线上消费者");
        var final=Enter(5062);battle.ObserveLukeRun(final[0].Identity.InstanceId,final,55846);Clear(5062,final);Exit(final);
        That(battle.HasPendingTransition&&battle.View.Timers.IsEmpty,"原P2终结立即停旧phase活动timer，仍等独立提交屏障");
        CommitDomainOnly();That(battle.View.State==AntonRaidBattleState.Completed,"原两阶段规则领域终结完成，非SQL/网络通关验收");

        foreach (int abandonedDungeon in new[] {5054, 5055, 5056})
        {
            var failure = new AntonRaidBattle(script, kind: RaidKind.Luke);
            foreach (var actor in actors) failure.JoinStart(actor);
            var id = Guid.NewGuid();
            var abandoned = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, id, 1))).ToArray();
            using (var reservation = failure.ReserveDungeon(new(failure.Instance, 0, 1, RaidKind.Luke), 5054, abandoned)!) reservation.Install();
            failure.ObserveLukeRun(id, abandoned);
            if (abandonedDungeon != 5054)
            {
                foreach (var actor in actors.Take(4)) failure.ReleaseDungeon(actor, id);
                id = Guid.NewGuid();
                abandoned = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, id, 1))).ToArray();
                using var reservation = failure.ReserveDungeon(new(failure.Instance, 0, 1, RaidKind.Luke), (uint)abandonedDungeon, abandoned)!;
                reservation.Install();
                failure.ObserveLukeRun(id, abandoned);
            }
            foreach (var actor in actors.Take(3)) failure.ReleaseDungeon(actor, id);
            That(!failure.View.Deferred.Values.Any(action => action.Tag == "[reset sanctum object]" && action.DungeonId == abandonedDungeon),
                "部分成员离开不重置当前圣所：" + abandonedDungeon);
            failure.ReleaseDungeon(actors[3], id);
            That(failure.View.Deferred.Values.Any(action => action.Tag == "[reset sanctum object]" && action.DungeonId == abandonedDungeon),
                "三个原GiveUp分支均只记录待消费对象重置：" + abandonedDungeon);
            var view = failure.View;
            failure.ReleaseDungeon(actors[3], id);
            That(ReferenceEquals(view, failure.View), "重复退出不重复GiveUp：" + abandonedDungeon);
            failure.EndLukePhase(failure.Instance, 0);
            That(failure.View.Deferred.Values.Any(action => action.Tag == "[fail phase]") && failure.View.Symbols["[kill all character]"] == 1,
                "原阶段到期生成失败/kill事实，尚未驱动真实全团死亡消费者");
        }
        That(actors.Select((actor,index)=>ReferenceEquals(actor.Session.Selected,characters[index])).All(value=>value),"原40条纯领域规则不修改真实角色、资产或伪造死亡");
        Console.WriteLine($"PASS {Count-before} 卢克当前40规则、九Run/route{route}与timer纯领域断言（非线上机制验收）");

        (RaidActor Actor,DungeonRunIdentity Identity)[] Enter(int dungeon,byte squad=1)
        {
            var id=Guid.NewGuid();var rows=actors.Skip((squad-1)*4).Take(4).Select(actor=>(actor,new DungeonRunIdentity(actor.Character,0,1,id,1))).ToArray();
            using var reservation=battle.ReserveDungeon(new(battle.Instance,battle.View.Phase,squad,RaidKind.Luke),(uint)dungeon,rows);
            That(reservation is not null,"完整原规则授权当前Open目标："+dungeon);reservation!.Install();battle.ObserveLukeRun(id,rows);return rows;
        }
        void Clear(int dungeon,(RaidActor Actor,DungeonRunIdentity Identity)[] rows)
        {
            var facts=rows.Select(row=>(row.Actor,new DungeonClearFact(row.Identity,(uint)dungeon,[Guid.NewGuid()])
                {ClearMap=new DungeonMap("boss",new(0,0),77,"fixture/boss.map")})).ToArray();
            battle.CompleteLukeDungeon(facts);var view=battle.View;battle.CompleteLukeDungeon(facts);
            That(ReferenceEquals(view,battle.View),"相同Run事实重放不重复HP/计数/timer规则");
        }
        void Exit((RaidActor Actor,DungeonRunIdentity Identity)[] rows){foreach(var row in rows)battle.ReleaseDungeon(row.Actor,row.Identity.InstanceId);}
        void CommitDomainOnly()
        {
            var candidate=battle.PendingLukeSettlement()!;
            // 仅检验解释器屏障的人工无奖回执；真实SQL/资格由独立Runtime及事务组证明。
            battle.AcceptLukeSettlement(candidate,new(candidate.Members.Select(member=>new RaidPhaseEligibility(candidate.Instance,RaidKind.Luke,
                candidate.Phase,candidate.Event,candidate.Anchor.ToUnixTimeSeconds(),member.Character,candidate.Phase==1,false,"fixture/domain-only",
                candidate.Policy.Rewards.Source,candidate.Week,candidate.Policy.Rewards.Offers)).ToImmutableArray()));
        }
    }

    // 133的原生来源尚未接通：这里只向最小领域夹具提供显式输入，保留原四条条件/动作。
    // 不改真实40规则，不注册Runtime策略，不把90/120秒分支测试称为完整原资源通关。
    static void EnergyBranches(RaidScript original, RaidActor[] actors)
    {
        int before = Count;
        var phase = original.Phases[1];
        var energy = phase.Dungeons.Single(dungeon => dungeon.Id == 5063);
        var rules = phase.Rules.Where(rule => rule.Trigger.Any(clause =>
            clause.Tag == "[on change dungeon state]" && clause.DungeonId == 5063 ||
            clause.Tag == "[check timer end]" && clause.Timer == new RaidTimer(2, 5063))).ToImmutableArray();
        That(rules.Length == 4, "能源夹具仅复用原45/90/120与恢复四条规则，资源变更须重新审查");
        foreach (var (input, seconds) in new[] {(0, 45), (1, 90), (2, 120)})
        {
            var initialize = new RaidRule([new("[if]", -1, null, "[2phase init]", null, null, [0])],
                [new("[set]", -1, null, "[2phase init]", null, null, [1]),
                 new("[set]", -1, null, "[energy control open]", null, null, [input])]);
            var fixture = original with
            {
                Path = "synthetic/luke-energy-input.etc",
                Phases = [phase with {Dungeons = [energy with {InitialState = RaidDungeonState.Open}], Rules = [initialize, .. rules]}]
            };
            var battle = new AntonRaidBattle(fixture, kind: RaidKind.Luke);
            foreach (var actor in actors) That(battle.JoinStart(actor), "能源领域夹具原actor登记");
            var id = Guid.NewGuid();
            var rows = actors.Take(4).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, id, 1))).ToArray();
            using (var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Luke), 5063, rows)!) reservation.Install();
            battle.ObserveLukeRun(id, rows);
            var facts = rows.Select(row => (row.Actor, new DungeonClearFact(row.Identity, 5063, [Guid.NewGuid()])
                {ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map")})).ToArray();
            battle.CompleteLukeDungeon(facts);
            var timer = new RaidTimer(2, 5063);
            That(battle.View.Timers[timer] == seconds && battle.View.Dungeons[5063] == RaidDungeonState.Close &&
                battle.View.Symbols["[energy control effect]"] == 0, "原能源条件选择明确秒值：" + input);
            var view = battle.View;
            battle.CompleteLukeDungeon(facts);
            That(ReferenceEquals(view, battle.View), "四成员清图重放不重置能源deadline：" + input);
            foreach (var row in rows) battle.ReleaseDungeon(row.Actor, id);
            long generation = battle.View.TimerGenerations[timer];
            That(battle.ExpireLukeTimer(Guid.NewGuid(), timer, generation) is null &&
                battle.ExpireLukeTimer(battle.Instance, timer, generation + 1) is null, "错误实例/代不重新开放能源");
            That(battle.ExpireLukeTimer(battle.Instance, timer, generation) is not null &&
                battle.View.Dungeons[5063] == RaidDungeonState.Open && battle.View.Symbols["[energy control effect]"] == 1 &&
                battle.ExpireLukeTimer(battle.Instance, timer, generation) is null,
                "原能源恢复只开放一次；effect符号事实不冒充怪物效果消费者");
        }
        Console.WriteLine($"PASS {Count - before} 卢克能源显式输入条件领域断言（非原生来源或TCP验收）");
    }

}
