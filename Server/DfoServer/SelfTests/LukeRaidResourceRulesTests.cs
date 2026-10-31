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
        That(LukeRaidPhaseTimeoutPolicy.Shared95.Matches(script) &&
            LukeRaidPhaseTimeoutPolicy.Shared95.Seconds.SequenceEqual(new[] { 2700, 3600 }),
            "本服45/60分钟显式策略仍严格匹配原资源35/40分钟，不改PVF声明");
        var pacing = new LukeRaidPacingPolicy("95jp/luke/shared-pacing-v1", 300, 600, 5);
        foreach (int count in new[] { 1, 4, 8 })
            for (int scenario = 0; scenario < 4; scenario++)
                RunRoute(script, actors.Take(count).ToArray(), completion, 1, pacing, scenario);
    }

    static void RunRoute(RaidScript script, RaidActor[] actors, LukeRaidCompletionPolicy completion, int route,
        LukeRaidPacingPolicy? pacing = null, int scenario = 0)
    {
        int before=Count;var characters=actors.Select(actor=>actor.Session.Selected).ToArray();
        That(script.Phases.Sum(phase=>phase.Rules.Length)==40&&script.Issues.IsEmpty,"直接消费当前完整40规则，不删除危险规则伪称原资源");
        if (pacing is not null)
        {
            var original = script;
            var adapted = pacing.Adapt(original);
            That(original.Phases.Sum(phase => phase.Rules.Length) == 40 && adapted.Phases.Sum(phase => phase.Rules.Length) == 38 &&
                ReferenceEquals(adapted.Phases[0], original.Phases[0]), "统一节奏只派生P2差量，不改原40条资源或P1");
            That(adapted.Phases[1].Rules.All(rule => !rule.Trigger.Any(clause => clause.Tag == "[on giveup dungeon]" &&
                clause.DungeonId is 5061 or 5062)), "离开任一王座均不再全团关闭能源/踢能源队");
        }
        var battle=new AntonRaidBattle(script,kind:RaidKind.Luke,completion:completion,pacing:pacing);
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
        That(battle.HasPendingTransition&&battle.PendingLukeSettlement()?.Members.Take(Math.Min(4, actors.Length)).All(member=>member.CommittedRuns==9)==true,
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
        if (pacing is not null)
        {
            UnifiedThrones();
            That(actors.Select((actor,index)=>ReferenceEquals(actor.Session.Selected,characters[index])).All(value=>value),
                "统一节奏纯领域检查不修改角色资产或伪造死亡");
            Console.WriteLine($"PASS {Count-before} Luke shared pacing members={actors.Length} scenario={scenario} domain-only");
            return;
        }
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
            That(failure.View.PhaseExpired && failure.View.Deferred.IsEmpty && failure.View.Timers.IsEmpty &&
                failure.View.Dungeons.Values.All(state => state == RaidDungeonState.Hide) && failure.View.Symbols["[kill all character]"] == 1,
                "原阶段到期提交失败/封图事实，真实安全退场由独立SQL协调者处理");
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

        void UnifiedThrones()
        {
            var attack = new RaidTimer(1, 5066); var recovery = new RaidTimer(2, 5066);
            That(battle.View.Dungeons[5066] == RaidDungeonState.Open && !battle.View.Timers.ContainsKey(attack) &&
                !battle.View.LukeEnergyActivated, "双圣殿后能源和王座同时开放，候战选图不提前增长");
            long initial = 0;
            byte energySquad = actors.Length > 4 ? (byte)2 : (byte)1;
            if (scenario == 1)
            {
                var early = Enter(5066, energySquad);
                initial = battle.View.TimerGenerations[attack];
                That(battle.View.LukeEnergyActivated && battle.View.Timers[attack] == 600, "首次实际进入能源启动十分钟增长");
                Clear(5066, early); Exit(early);
                That(!battle.View.Timers.ContainsKey(attack) && battle.View.Timers[recovery] == 300,
                    "可先压能源，恢复窗口五分钟，攻击旧代失效");
                That(battle.ExpireLukeTimer(battle.Instance, attack, initial) is null, "压能源后迟到攻击时钟不触发满能源");
            }
            var light = Enter(5061); battle.ObserveLukeRun(light[0].Identity.InstanceId, light, 55845);
            if (scenario != 1) initial = battle.View.TimerGenerations[attack];
            That(battle.View.Dungeons[5061] == RaidDungeonState.Open, "未压能源也可直接进入光王座Boss地图");
            (RaidActor Actor, DungeonRunIdentity Identity)[] concurrent = [];
            if (scenario >= 2 && actors.Length == 8) concurrent = Enter(5066, 2);
            long target = battle.View.DungeonGenerations[5066];
            if (scenario is 0 or 3)
            {
                That(battle.ExpireLukeTimer(battle.Instance, attack, initial) is not null &&
                    battle.View.Symbols["[energy save effect]"] == 2 && battle.View.Symbols["[kill all character]"] == 0 &&
                    battle.View.LukeEnergyBlocked && battle.View.Dungeons[5066] == RaidDungeonState.Open,
                    "满能源保留effect2并锁王座，安全退场不产生KILL死亡信号");
                That(light.All(row => battle.RequiresLukeEnergyReturn(row.Actor, row.Identity.InstanceId)), "只标记当时王座原Run等待安全退场");
                var blocked = battle.View;
                Clear(5061, light);
                That(ReferenceEquals(blocked, battle.View), "满能源后迟到王座清图不再推进通关");
                Exit(light);
                var blockedId = Guid.NewGuid();
                var blockedRows = actors.Take(4).Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, blockedId, 1))).ToArray();
                using var denied = battle.ReserveDungeon(new(battle.Instance, 1, 1, RaidKind.Luke), 5061, blockedRows);
                That(denied is null, "满能源后不能通过再次选图绕过真实压制");
                That(concurrent.All(row => !battle.RequiresLukeEnergyReturn(row.Actor, row.Identity.InstanceId)),
                    "满能源不标记或退出并行能源队");
                That(battle.View.Symbols["[kick out character]"] == 0 && battle.View.DungeonGenerations[5066] == target,
                    "慢打退出光王座不踢能源队或重建能源目标");
                var pressed = concurrent.Length == 0 ? Enter(5066, energySquad) : concurrent;
                Clear(5066, pressed); Exit(pressed); concurrent = [];
                That(!battle.View.LukeEnergyBlocked && battle.View.Symbols["[energy save effect]"] == 0 && battle.View.Timers[recovery] == 300,
                    "满能源后仍须真实清图压制，不自动完成");
                light = Enter(5061); battle.ObserveLukeRun(light[0].Identity.InstanceId, light, 55845);
                That(light.All(row => !battle.RequiresLukeEnergyReturn(row.Actor, row.Identity.InstanceId)), "压制后新王座Run不受旧退场标记影响");
            }
            var beforeLightClear = battle.View;
            Clear(5061, light); Exit(light);
            That(battle.View.Dungeons[5062] == RaidDungeonState.Open &&
                battle.View.Dungeons[5066] == beforeLightClear.Dungeons[5066] &&
                battle.View.Symbols["[energy save effect]"] == beforeLightClear.Symbols["[energy save effect]"],
                "光王座清图开放暗王座，能源开闭及效果原样延续");
            if (scenario == 2)
            {
                That(battle.View.TimerGenerations[attack] == initial && battle.View.DungeonGenerations[5066] == target,
                    "光王座快打完成不暂停或重置正在增长的能源");
                var pressed = concurrent.Length == 0 ? Enter(5066, energySquad) : concurrent;
                Clear(5066, pressed); Exit(pressed);
                That(battle.View.Dungeons[5066] == RaidDungeonState.Close && battle.View.Symbols["[energy save effect]"] == 0,
                    "先打完光王座后再压能源；并行能源Run也能完成原目标");
            }
            else
            {
                That(!battle.View.Timers.ContainsKey(attack) && battle.View.Timers[recovery] == 300 &&
                    battle.View.TimerGenerations[recovery] == beforeLightClear.TimerGenerations[recovery] &&
                    battle.View.TimerAnchors[recovery] == beforeLightClear.TimerAnchors[recovery],
                    "光王座通关不重开能源、不清零压制剩余时长");
            }
            var dark = Enter(5062); battle.ObserveLukeRun(dark[0].Identity.InstanceId, dark, 55846);
            Exit(dark);
            var retained = battle.View;
            That(retained.Symbols["[kick out character]"] == 0 && retained.Dungeons[5062] == RaidDungeonState.Open,
                "退出暗王座可继续压能源，已完成光王座不会回退");
            if (retained.Timers.TryGetValue(recovery, out _))
            {
                long old = retained.TimerGenerations[recovery];
                That(battle.ExpireLukeTimer(battle.Instance, recovery, old) is not null &&
                    battle.ExpireLukeTimer(battle.Instance, recovery, old) is null, "恢复只重开一次能源");
            }
            var lastPress = Enter(5066, energySquad); Clear(5066, lastPress); Exit(lastPress);
            dark = Enter(5062); battle.ObserveLukeRun(dark[0].Identity.InstanceId, dark, 55846); Clear(5062, dark); Exit(dark);
            That(battle.HasPendingTransition && battle.View.Timers.IsEmpty, "同一成员可真实完成两路及暗王座终结，终结停时钟");
            CommitDomainOnly();
            That(battle.View.State == AntonRaidBattleState.Completed, "统一规则完成两阶段领域流程，不代表SQL或客户端验收");
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
