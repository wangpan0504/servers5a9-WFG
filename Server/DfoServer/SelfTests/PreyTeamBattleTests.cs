using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamBattleTests
{
    internal static void RunSpawnReservations(RaidScript script)
    {
        var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyRandom: new Random(19), preyGuide: false);
        int[] initial = Enumerable.Range(1, 16).Where(slot => battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 5).ToArray();
        var defences = script.Phases[0].Dungeons.Where(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "slenikon defence" }])).ToArray();
        That(initial.Length == 2 && initial.Select(slot => battle.View.Symbols[$"[area_{slot}]"]).Distinct().Count() == 2 &&
            initial.All(slot => defences.Any(row => row.Id == battle.View.Symbols[$"[area_{slot}]"] && row.InitialState == RaidDungeonState.Open)) &&
            initial.All(slot => slot is not (1 or 5 or 9 or 13)),
            "原普通开局两处真实阻击占不同非圣地区域，模板来自本阶段Open声明");
        void Expire(RaidTimer timer) => That(battle.ExpireLukeTimer(battle.Instance, timer,
            battle.View.TimerGenerations[timer]) is not null, "当前原代魔塔计时事件执行");
        Expire(new(9000, 0));
        That(initial.All(slot => battle.AllowsPreyEntry(battle.View.Symbols[$"[area_{slot}]"])),
            "初始阻击在准备完成后即可入场，不等待魔塔180秒周期");
        var towers = script.Phases[0].Dungeons.Where(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "magic tower" }])).ToArray();
        That(towers.Length == 2, "原普通资源包含两座独立魔塔");
        int Position(int tower) => battle.View.Symbols[$"[{tower} magic tower spawn position]"];
        int[] reserved = towers.Select(tower => Position(tower.Id)).ToArray();
        That(reserved.All(position => position is >= 301 and <= 316) && reserved.Distinct().Count() == towers.Length &&
            reserved.All(position => battle.View.Symbols[$"[area_{position - 300}]"] == 0),
            "准备结束公布不同的空白生成点，两塔不能预告同一位置");
        // 保留开局阻击；后续生成仍须在两塔预告空格落地，不能因同种模板已存在而漏掉整波。
        for (int index = 0; index < towers.Length; index++)
        {
            Expire(new(500, towers[index].Id));
            int slot = reserved[index] - 300;
            That(battle.View.Symbols[$"[area_{slot}]"] > 0 && battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 5,
                "阻击在本塔已经公布的原位置生成，不挪到另一空格");
            That(Position(towers[0].Id) != Position(towers[1].Id),
                "原波生成并预留下一波后，两塔待生成位置仍互斥");
        }
        PreyTeamSpawnTests.Run(script);
    }

    internal static void RunMembers(RaidActor[] actors)
    {
        if (actors.Length < 3) throw new ArgumentException("普通普雷分队回归需要三个既有角色", nameof(actors));
        var markerScript = Script();
        markerScript = markerScript with { Symbols = markerScript.Symbols.SetItems(Enumerable.Range(1, 20)
            .Select(slot => new KeyValuePair<string, int>($"[area_{slot}]", 300 + slot))) };
        var battle = new AntonRaidBattle(markerScript, kind: RaidKind.Prey, preyRandom: new Random(17), preyGuide: false);
        foreach (var actor in actors.Take(3)) That(battle.JoinStart(actor), "普通模式登记本团原角色");
        var preparing = battle.View;
        That(battle.CompletePreyBoardingPreparation(actors[0]) is null && ReferenceEquals(preparing, battle.View) &&
            battle.PreyPreparationPending(preparing) && battle.PreyPreparationSeconds(preparing) is > 0,
            "提前登船36只确认场景就绪，不能消耗仍在进行的开场准备计时");
        var located = battle.View;
        That(battle.ReportPreyTownLocation(actors[0], 2) is null &&
            battle.ReportPreyTownLocation(actors[0], 1) is null && battle.ReportPreyTownLocation(actors[0], 4) is null &&
            battle.ReportPreyTownLocation(actors[0] with { Epoch = actors[0].Epoch + 1 }, 2) is null &&
            ReferenceEquals(located, battle.View) && battle.View.PreyMemberLocations.IsEmpty,
            "不在59号城镇的合成角色、未提交区域和旧选角身份不改变玩家位置展示");
        battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]);
        That(battle.SetPreyMarker(actors[0], 0, 301) is not null && battle.View.PreyMarkers.Count == 1 &&
            battle.View.PreyMarkers[0] == 301, "普通标记保留当前阶段真实区域symbol，不是DGN号");
        var marked = battle.View;
        That(battle.SetPreyMarker(actors[0], 0, 301) is null && ReferenceEquals(marked, battle.View) &&
            battle.SetPreyMarker(actors[0], 3, 301) is null && battle.SetPreyMarker(actors[0], 0, 317) is null &&
            battle.SetPreyMarker(actors[0], 0, 320) is null, "重复、第四组及下一阶段区域不更改当前标记");
        That(battle.SetPreyMarker(actors[0], 0, 0) is not null && battle.View.PreyMarkers.IsEmpty,
            "清除标记不影响阶段地图、参与或正式副本状态");
        That(battle.AllowsPreyEntry(10), "普通开局的真实阻击在准备完成后可立即预留进图");
        RemoveDefenceAreas(battle, markerScript);
        That(!battle.AllowsPreyEntry(10), "已腾空阻击AREA的Open模板不能凭原型状态继续入图");
        battle.ExpireLukeTimer(battle.Instance, new(500, 11), battle.View.TimerGenerations[new(500, 11)]);
        That(battle.AllowsPreyEntry(10), "原魔塔180秒生成事件落实防御区域后才允许真实两队清图");
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var firstRows = actors.Take(2).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, first, 1))).ToArray();
        var secondRows = new[] { (Actor: actors[2], Identity: new DungeonRunIdentity(actors[2].Character, 0, 1, second, 1)) };
        using var one = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 101 }, 10, firstRows);
        using var duplicate = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 101 }, 11, secondRows);
        That(one is not null && duplicate is null, "同一实际Party不能以另一Run和不同成员重复预留另一个目标");
        using var two = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 102 }, 11, secondRows);
        That(two is not null, "不同实际Party可同时预留不同目标，其中一队保留双成员名册");
        one!.Install(); two!.Install();
        That(battle.ObservePreyTeamRun(first, firstRows, 77) is null || battle.View.State == AntonRaidBattleState.Fighting,
            "正式全员房间观察不抢先签发清图或终局");
        var firstFacts = firstRows.Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, 10, [Guid.NewGuid()])
            { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-team.map") })).ToArray();
        int defenceSlot = Enumerable.Range(1, 16).Single(slot => battle.View.Symbols[$"[area_{slot}]"] == 10);
        That(battle.CompletePreyDungeon([firstFacts[0]]) is null, "共享Run缺少队友真实清图事实时不部分消费");
        That(battle.CompletePreyDungeon(firstFacts) is not null && battle.HasDungeonContribution(actors[0], first) &&
            battle.HasDungeonContribution(actors[1], first) && battle.View.Dungeons[11] == RaidDungeonState.Open &&
            battle.DungeonSnapshot(battle.View).Single(row => row.Id == 10).State == RaidDungeonState.Hide &&
            battle.View.Symbols[$"[area_{defenceSlot}_dgn_type]"] == 7 &&
            !Enumerable.Range(1, 16).Any(slot => battle.View.Symbols[$"[area_{slot}]"] == 10),
            "全员事实一次清除防御目标并从面板隐藏，保留另一队进行中的目标");
        That(battle.CompletePreyDungeon(firstFacts) is null, "普通共享Run重复清图不累计参与次数");
        battle.ReleaseDungeon(actors[0], first); battle.ReleaseDungeon(actors[1], first);
        var secondFact = new DungeonClearFact(secondRows[0].Identity, 11, [Guid.NewGuid()])
            { ClearMap = new DungeonMap("boss", new(0, 0), 78, "fixture/prey-tower.map") };
        That(battle.CompletePreyDungeon([(actors[2], secondFact)]) is not null && battle.View.Dungeons[11] == RaidDungeonState.Clear,
            "另一队可继续提交原实例事实，不因全团revision变化失效");
        battle.ReleaseDungeon(actors[2], second);
        DungeonDeadlines(actors);
        GardenClearCounting(actors);
        TowerGiveUp(actors);
        SharedRevival(actors, 0, 3);
        SharedRevival(actors, 1, 4);
        PreyTeamDefenceTests.Run(actors, Script());
    }

    static void TowerGiveUp(RaidActor[] actors)
    {
        var battle = new AntonRaidBattle(Script(), kind: RaidKind.Prey, preyRandom: new Random(29), preyGuide: false);
        foreach (var actor in actors.Take(2)) That(battle.JoinStart(actor), "魔塔撤退复用当前两名成员");
        battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]);
        RemoveDefenceAreas(battle, Script());
        int slot = battle.View.Symbols["[11 magic tower spawn position]"] - 300;
        var timers = battle.View.Timers;
        var anchors = battle.View.TimerAnchors;
        var generations = battle.View.TimerGenerations;
        Guid instance = Guid.NewGuid();
        var rows = actors.Take(2).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, instance, 1))).ToArray();
        using var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
            { PreyGuide = false, PreyPartyId = 401 }, 11, rows);
        That(reservation is not null, "魔塔建立同一正式共享Run");
        reservation!.Install();
        battle.ReleaseDungeon(actors[0], instance);
        That(battle.View.Symbols[$"[area_{slot}]"] == 0, "一人退出、队友仍在图内不算魔塔失败");
        battle.ReleaseDungeon(actors[1], instance);
        That(battle.View.Symbols[$"[area_{slot}]"] == 10 && battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 5 &&
            battle.DungeonSnapshot(battle.View).Single(row => row.Id == 10).State == RaidDungeonState.Open,
            "最后成员未清图退出立即在本塔预告区生成真实阻击");
        That(ReferenceEquals(timers, battle.View.Timers) && ReferenceEquals(anchors, battle.View.TimerAnchors) &&
            ReferenceEquals(generations, battle.View.TimerGenerations), "失败即时生成不猜测或重置原PVF周期");
        var committed = battle.View;
        That(battle.ReleaseDungeon(actors[1], instance) is null && ReferenceEquals(committed, battle.View),
            "旧Run重复释放不再生成阻击或改写计时器");
    }

    static void SharedRevival(RaidActor[] actors, int phase, int limit)
    {
        var clock = new LukeRaidClockTests.Clock();
        var battle = new AntonRaidBattle(Script(), kind: RaidKind.Prey, clock: clock, preyGuide: false);
        var policies = Enumerable.Range(0, 2).Select(index => new RaidPhaseRewardPolicy("fixture/prey-coins",
            [new(0, 1, [new(100, 1)], [])], new(0, (byte)index, 1, 0, 0, 0))).ToImmutableArray();
        foreach (var actor in actors.Take(3)) That(battle.Join(actor, policies), "共享复活绑定当前普通普雷成员");
        battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]);
        if (phase == 1)
        {
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[first complete]", 1);
            foreach (var actor in actors.Take(3))
                battle.ConfirmReward(actor, new(battle.Instance, 0, 0, 1, actor.Character, "fixture/prey-coins", [new(100, 1)], false, 1)
                    { RaidType = RaidKind.Prey });
            That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null, "共享复活独立覆盖普通二阶段");
        }
        uint dungeon = phase == 0 ? 11u : 20u, other = phase == 0 ? 12u : 21u;
        var rows = Enter(dungeon, actors.Take(2).ToArray(), 501);
        (RaidActor Actor, DungeonRunIdentity Identity)[] otherRows;
        That(battle.ReportStatusHp(rows[0].Actor, rows[0].Identity,
            new(0, [.. rows.Select(row => row.Actor.Character)], 99, 17, [73, 41])) is not null,
            "共享配额前接受原HP上下文，客户端旧用币字段不能覆盖权威额度");
        using (var failed = battle.ReserveLukeRevival(rows[0].Actor, rows[0].Identity, 1))
        {
            That(failed is not null, "扣币前只预留，失败释放不消耗共享额度");
            otherRows = Enter(other, [actors[2]], 502);
            That(otherRows.Length == 1, "一队的在途扣币不以共同Squad0阻挡另一实际小队入图");
        }
        for (int used = 0; used < limit; used++)
        {
            var row = rows[used % rows.Length];
            long death = used / rows.Length + 1;
            using var token = battle.ReserveLukeRevival(row.Actor, row.Identity, death);
            That(token is not null && battle.ReserveLukeRevival(row.Actor, row.Identity, death) is null,
                "同小队成员交替消费原DGN上限，同一次死亡不能并发占两格");
            if (used + 1 == limit)
                That(battle.ReserveLukeRevival(rows[(used + 1) % rows.Length].Actor,
                    rows[(used + 1) % rows.Length].Identity, 100) is null, "最后一格预留时队友不能超额预留");
            long revision = battle.View.Revision;
            token!.Commit(); token.Commit();
            var status = battle.StatusSnapshot(battle.View).Single(display => display.Run == row.Identity.InstanceId);
            That(status.UsedCoins == used + 1 && status.Context == 17 && status.Values.SequenceEqual(new uint[] { 73, 41 }) &&
                battle.View.Revision == revision + 1 && token.Change?.After == battle.View && battle.View.LukePhaseCoinsUsed is null,
                "普通两阶段逐次提交并发布本Run共享用币，保留HP、不借用卢克全团计数");
            That(battle.ReserveLukeRevival(row.Actor, row.Identity, death) is null, "已提交死亡事实不能再次占用额度");
        }
        That(battle.ReserveLukeRevival(rows[1].Actor, rows[1].Identity, 100) is null,
            "本人尚未达到个人次数也不能突破小队共享上限");
        using (var independent = battle.ReserveLukeRevival(otherRows[0].Actor, otherRows[0].Identity, 1))
        {
            That(independent is not null, "另一小队Run保留自己的复活额度");
            independent!.Commit();
        }
        foreach (var row in rows) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var retry = Enter(dungeon, actors.Take(2).ToArray(), 501);
        clock.AdvanceWithoutCallbacks(5);
        That(battle.ReserveLukeRevival(retry[0].Actor, retry[0].Identity, 1) is null,
            "单图期限到达而时钟尚未回调时也不能再预留复活");

        (RaidActor Actor, DungeonRunIdentity Identity)[] Enter(uint target, RaidActor[] members, ushort party)
        {
            Guid instance = Guid.NewGuid();
            var entries = members.Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, instance, 1))).ToArray();
            using var entry = battle.ReserveDungeon(new(battle.Instance, phase, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = party }, target, entries, coinLimit: limit, timeLimitSeconds: 5);
            That(entry is not null, "每个新正式Run冻结本次配置的复活次数与期限");
            entry!.Install();
            using var available = battle.ReserveLukeRevival(entries[0].Actor, entries[0].Identity, 1);
            That(available is not null, "新Run不继承旧Run的已用或未提交预留");
            return entries;
        }
    }

    static void GardenClearCounting(RaidActor[] actors)
    {
        var source = Script();
        var gardens = Enumerable.Range(30, 2).Select(id => new RaidDungeon(id, RaidDungeonState.Open, $"[hp{id}]", 1, 1)
        { Declarations = [new("[type]", 0, [new(6, 0, "garden")])] }).ToImmutableArray();
        var script = source with
        {
            Symbols = source.Symbols.Add("[clear garden]", 224).Add("[hp30]", 225).Add("[hp31]", 226),
            Phases = source.Phases.SetItem(0, source.Phases[0] with { Dungeons = [.. source.Phases[0].Dungeons, .. gardens] })
        };
        var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: false);
        foreach (var actor in actors.Take(2)) That(battle.JoinStart(actor), "庭院计数用例复用两名当前成员");
        battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]);
        That(battle.View.Symbols["[clear garden]"] == 0, "新普通团在任何正式清图前庭院计数为0");
        for (int index = 0; index < gardens.Length; index++)
        {
            Guid instance = Guid.NewGuid();
            var rows = actors.Take(2).Select(actor => (Actor: actor,
                Identity: new DungeonRunIdentity(actor.Character, 0, index + 1, instance, 1))).ToArray();
            using var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 301 }, checked((uint)gardens[index].Id), rows);
            That(reservation is not null, "两名成员共享同一个真实绑定的庭院Run");
            reservation!.Install();
            var facts = rows.Select(row => (row.Actor, Fact: new DungeonClearFact(row.Identity, checked((uint)gardens[index].Id), [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-garden.map") })).ToArray();
            That(battle.CompletePreyDungeon(facts) is not null && battle.View.Symbols["[clear garden]"] == index + 1,
                "共享Run按已清庭院数量计1/2，不按实际清图成员数叠加");
            var committed = battle.View;
            That(battle.CompletePreyDungeon(facts) is null && ReferenceEquals(committed, battle.View) &&
                battle.View.Symbols["[clear garden]"] == index + 1, "同一庭院重复清图不叠加攻击增益计数");
            foreach (var row in rows) battle.ReleaseDungeon(row.Actor, instance);
        }
        That(new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: false).View.Symbols["[clear garden]"] == 0,
            "重开新普通团不会继承上一团两座庭院的攻击增益");
    }

    static void DungeonDeadlines(RaidActor[] actors)
    {
        var clock = new LukeRaidClockTests.Clock();
        var errors = new List<string>();
        var clocks = new LukeRaidClocks(clock, new DeadlineStore(), (stage, _) => errors.Add(stage));
        try
        {
            var battle = new AntonRaidBattle(Script(), kind: RaidKind.Prey, clock: clock, preyRandom: new Random(3), preyGuide: false);
            foreach (var actor in actors.Take(3)) That(battle.JoinStart(actor), "单图期限引用原当前成员");
            battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]);
            RemoveDefenceAreas(battle, Script());
            using var lease = clocks.Reserve(battle)!;
            lease.Activate();
            Guid original = Guid.NewGuid(), other = Guid.NewGuid();
            var rows = actors.Take(2).Select(actor => (Actor: actor, Identity: new DungeonRunIdentity(actor.Character, 0, 1, original, 1))).ToArray();
            var otherRows = new[] { (Actor: actors[2], Identity: new DungeonRunIdentity(actors[2].Character, 0, 1, other, 1)) };
            using var first = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 201 },
                11, rows, timeLimitSeconds: 5);
            using var second = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 202 },
                12, otherRows, timeLimitSeconds: 20);
            That(first is not null && second is not null, "两队分别冻结各自原图期限");
            clock.AdvanceWithoutCallbacks(10);
            That(battle.PreyDungeonSeconds(actors[0], rows[0].Identity) is null,
                "入场准备尚未安装不能发布单图时钟");
            first!.Install(); second!.Install();
            That(battle.PreyDungeonSeconds(actors[0], rows[0].Identity) == 5 &&
                battle.PreyDungeonSeconds(actors[2], otherRows[0].Identity) == 20 &&
                battle.PreyDungeonSeconds(actors[2], rows[0].Identity) is null,
                "单图时钟分别读取各队已安装Run期限，拒绝其它成员身份");
            clock.Advance(4);
            That(battle.PreyDungeonSeconds(actors[0], rows[0].Identity) == 1 &&
                battle.PreyDungeonSeconds(actors[0], rows[0].Identity with { TaskAttempt = 2 }) is null,
                "显示恢复只取原期限余时，旧尝试不能发布");
            That(battle.View.PreyExpelledRuns.IsEmpty, "入场准备等待不计时，正式安装后期限前一秒仍可战斗");
            clock.AdvanceWithoutCallbacks(1);
            That(battle.PreyDungeonSeconds(actors[0], rows[0].Identity) is null,
                "期限已到、定时回调尚未执行也不发布正数单图余时");
            var fact = new DungeonClearFact(rows[0].Identity, 11, [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-deadline.map") };
            That(battle.CompletePreyDungeon(rows.Select(row => (row.Actor, Fact: fact with { Identity = row.Identity })).ToArray()) is null,
                "期限恰好到达但时钟尚未回调时，迟到清图事实不能授予贡献");
            clock.Advance(0);
            That(battle.View.PreyExpelledRuns.SetEquals([original]) && !battle.View.PhaseExpired &&
                battle.View.State == AntonRaidBattleState.Fighting && battle.View.Dungeons[11] == RaidDungeonState.Open &&
                !battle.HasDungeonContribution(actors[0], original) && !battle.HasDungeonContribution(actors[1], original),
                "原单图期限自然到期只踢对应共享Run，不关闭整团、不清图或签发资格");
            That(Enumerable.Range(1, 16).Any(slot => battle.View.Symbols[$"[area_{slot}]"] == 10),
                "魔塔单图超时沿同一GiveUp链立即生成阻击");
            battle.ReleaseDungeon(actors[0], original); battle.ReleaseDungeon(actors[1], original);
            Guid next = Guid.NewGuid();
            var nextRows = rows.Select(row => (row.Actor, Identity: row.Identity with { InstanceId = next, TaskAttempt = 2 })).ToArray();
            using var retry = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey) { PreyGuide = false, PreyPartyId = 201 },
                11, nextRows, timeLimitSeconds: 5);
            That(retry is not null, "无重试限制的合成目标可建立新的正式Run");
            retry!.Install();
            That(battle.PreyDungeonSeconds(actors[0], nextRows[0].Identity) == 5 &&
                battle.PreyDungeonSeconds(actors[0], rows[0].Identity) is null,
                "后续挑战只显示新Run自己的期限，旧Run不得重设UI时钟");
            clock.FireQueued(); clock.Advance(4);
            That(battle.View.PreyExpelledRuns.IsEmpty, "旧已排队时钟不能提前踢出相同目标后续Run，另一队仍在原图");
            clock.Advance(1);
            That(battle.View.PreyExpelledRuns.SetEquals([next]) && errors.Count == 0,
                "后续Run只按自己的安装锚点到期，不复用旧deadline或触发奖励存储");
        }
        finally { clocks.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    sealed class DeadlineStore : ILukeRaidLifecycleStore
    {
        public bool IsRetryable(Exception error) => false;
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) =>
            throw new InvalidOperationException("单图失败不能提交阶段资格");
    }

    internal static void RunCore()
    {
        int before = Count;
        AdmissionRules();
        ShipRoute();
        PreyTeamLandingTests.RunCore(Script());
        var clock = new LukeRaidClockTests.Clock();
        var battle = new AntonRaidBattle(Script(), kind: RaidKind.Prey, clock: clock, preyRandom: new Random(7), preyGuide: false);
        That(!battle.PreyGuide && battle.View.Symbols["[slenikon hp]"] == 10000 && battle.View.Symbols["[isys hp]"] == 10000,
            "普通普雷保留自身模式、船体和伊希斯初始HP");
        var positions = Enumerable.Range(1, 16).Select(slot => battle.View.Symbols[$"[area_{slot}]"]).Where(id => id != 0).ToArray();
        That(positions.Length == 6 && positions.Distinct().Count() == 6 && positions.Order().SequenceEqual(new[] { 10, 11, 12, 13, 14, 15 }) &&
            new[] { 1, 5, 9, 13 }.Select(slot => battle.View.Symbols[$"[area_{slot}]"] is >= 12 and <= 15 &&
                battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 4).All(value => value),
            "四圣地位置固定在1/5/9/13，怪物与其余初始据点随机分配；合成资源只有一种阻击时不补造第二种");
        That(Enumerable.Range(0, 12).Select(seed =>
            new AntonRaidBattle(Script(), kind: RaidKind.Prey, preyRandom: new Random(seed), preyGuide: false)
                .View.Symbols["[area_1]"]).Distinct().Count() > 1,
            "不同攻坚实例的同号圣地可以分配不同怪物，不再按资源声明顺序固定");
        That(battle.View.Dungeons[10] == RaidDungeonState.Open &&
            battle.DungeonSnapshot(battle.View).Single(row => row.Id == 10).State == RaidDungeonState.Open,
            "初始真实阻击的Open状态与AREA一致，不误投影为Hide");
        That(Enumerable.Range(1, 20).Where(slot => battle.View.Symbols[$"[area_{slot}]"] == 0)
            .All(slot => battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 7),
            "原生空区域使用TYPE7关闭卡片，不以TYPE0普通据点冒充空格");
        var noWeight = Script();
        noWeight = noWeight with { Phases = noWeight.Phases.SetItem(0, noWeight.Phases[0] with
        {
            Dungeons = noWeight.Phases[0].Dungeons.Select(row => row.Id == 10 ? row with
                { Declarations = row.Declarations.Select(field => field.Tag == "[rate]" ? field with { Values = [new(0, 0, null)] } : field).ToImmutableArray() } : row).ToImmutableArray()
        }) };
        var unselected = new AntonRaidBattle(noWeight, kind: RaidKind.Prey, preyGuide: false);
        That(!Enumerable.Range(1, 16).Any(slot => unselected.View.Symbols[$"[area_{slot}]"] == 10),
            "零权重原型不进入初始抽选，也不因不足两种正权重阻击拒绝构造");
        That(!battle.AllowsPreyEntry(11), "普通模式准备期内不接受提前进图");
        Expire(new(9000, 0));
        That(battle.AllowsPreyEntry(11) && battle.View.Timers.ContainsKey(new(500, 11)) &&
            battle.View.Timers.ContainsKey(new(10001, 150)), "普通准备完成开放据点，500组与raid timer命名空间独立");
        RemoveDefenceAreas(battle, Script());
        That(!battle.AllowsPreyEntry(10) && battle.DungeonSnapshot(battle.View).Single(row => row.Id == 10).State == RaidDungeonState.Hide,
            "显式腾空初始阻击后原型保持Open，实际AREA和面板隐藏仍是入场条件");
        That(battle.View.Timers[new(9001, 0)] == 1, "普通船体每秒结算一次，不把阶段速率误作间隔");
        Expire(new(9001, 0));
        That(battle.View.Symbols["[slenikon hp]"] == 9998, "普通一阶段每秒损失HP DECREASE首列2点");
        long originalTimer = battle.View.TimerGenerations[new(500, 11)];
        Expire(new(500, 11));
        That(battle.View.Symbols["[merchant]"] == 3 &&
            battle.ExpireLukeTimer(battle.Instance, new(500, 11), originalTimer) is null && battle.AllowsPreyEntry(10) &&
            battle.DungeonSnapshot(battle.View).Single(row => row.Id == 10).State == RaidDungeonState.Open,
            "500组原事件生成实际阻击后才显示Open，迟到原代不能再次消费");
        Expire(new(10001, 150));
        That(battle.View.Symbols["[merchant]"] == 0, "独立raid timer按自己的结束事件关闭商人");

        // 使用独立合成实例开启商人后立即过阶段；本夹具的500计时器是一次性事件，不能重复消费。
        clock = new LukeRaidClockTests.Clock();
        battle = new AntonRaidBattle(Script(), kind: RaidKind.Prey, clock: clock, preyRandom: new Random(7), preyGuide: false);
        Expire(new(9000, 0));
        Expire(new(500, 11));
        var merchant = new RaidTimer(10001, 150);
        long merchantGeneration = battle.View.TimerGenerations[merchant];
        That(battle.PreyTimerSeconds(battle.View, merchant) is > 0, "商人专用计时器可投影原生剩余秒数");

        battle.SetMechanism(battle.Instance, battle.View.Revision, "[slenikon hp]", 1500);
        Expire(new(9001, 0));
        That(battle.View.Symbols["[slenikon armor level]"] == 2, "一阶段体力穿过两档边界后铠甲完全破碎");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[first complete]", 1);
        That(battle.View.State == AntonRaidBattleState.Reward, "普通一阶段CLEAR PHASE产生奖励状态");
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null && battle.View.Phase == 1 &&
            battle.View.Symbols["[slenikon hp]"] == 10000 && battle.View.Symbols["[slenikon armor level]"] == 2 &&
            battle.View.Symbols["[isys hp]"] == 10000, "普通二阶段船体回满，保留一阶段铠甲破碎及伊希斯状态");
        That(battle.View.Symbols["[area_20_dgn_type]"] == 6, "普通二阶段资源AREA写入同步Boss区域类型");
        That(battle.View.Symbols["[merchant]"] != 0 && battle.View.TimerGenerations[merchant] == merchantGeneration,
            "二阶段继承同一商人计时义务，不重新赠送期限或丢失到期事件");
        Expire(merchant);
        That(battle.View.Symbols["[merchant]"] == 0, "跨阶段商人到期后关闭，不永久驻留");
        Expire(new(9001, 0));
        That(battle.View.Symbols["[slenikon hp]"] == 9983 && battle.View.Symbols["[slenikon armor level]"] == 2,
            "普通二阶段每秒损失17点体力，已破碎铠甲不会因高体力恢复");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[isys hp in dungeon]", 8000);
        Expire(new(2, 211));
        That(battle.View.Symbols["[isys hp]"] == 8000 && battle.View.Symbols["[isys groggy]"] == 0 &&
            battle.View.Symbols["[kick observed]"] == 1 && battle.View.Dungeons[20] == RaidDungeonState.Close,
            "虚弱到期提交房内HP，执行踢出回调及后继据点关闭");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[isys hp]", 0);
        That(battle.View.State == AntonRaidBattleState.Reward, "共享HP归零才产生普通终阶段结果");

        var failure = new AntonRaidBattle(Script(), kind: RaidKind.Prey, clock: clock, preyRandom: new Random(9), preyGuide: false);
        failure.ExpireLukeTimer(failure.Instance, new(9000, 0), failure.View.TimerGenerations[new(9000, 0)]);
        failure.SetMechanism(failure.Instance, failure.View.Revision, "[slenikon hp]", 2);
        failure.ExpireLukeTimer(failure.Instance, new(9001, 0), failure.View.TimerGenerations[new(9001, 0)]);
        That(failure.View.PhaseExpired && failure.View.Timers.IsEmpty && failure.View.State != AntonRaidBattleState.Reward &&
            !failure.AllowsPreyEntry(11), "普通船体耗尽关闭入口和时钟，不伪造奖励或通关");
        Console.WriteLine($"PASS {Count - before} 普雷普通战场、计时命名空间和终局规则断言");

        void Expire(RaidTimer timer) => That(battle.ExpireLukeTimer(battle.Instance, timer, battle.View.TimerGenerations[timer]) is not null,
            "当前原代计时器执行：" + timer);
    }

    // 仅用于生成/失败分支的合成准备，不把手工腾空当成正式清图或贡献事实。
    internal static void RemoveDefenceAreas(AntonRaidBattle battle, RaidScript script)
    {
        var defences = script.Phases[0].Dungeons.Where(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "slenikon defence" }])).Select(row => row.Id).ToHashSet();
        foreach (int slot in Enumerable.Range(1, 16).Where(slot => defences.Contains(battle.View.Symbols[$"[area_{slot}]"])).ToArray())
            battle.SetMechanism(battle.Instance, battle.View.Revision, $"[area_{slot}]", 0);
    }

    static void ShipRoute()
    {
        var source = Script();
        var timer = new RaidTimer(2, 1000);
        RaidPhase Flight(RaidPhase phase, int start, int speed)
        {
            RaidClause Set(string symbol, int value) => new("[set]", 0, null, symbol, null, null, [value]);
            var next = new RaidClause("[slenikon]", 0, null, null, null, null, []);
            var setTimer = new RaidClause("[set timer]", 0, null, null, null, timer, [timer.Kind, timer.DungeonId, speed]);
            return phase with { Rules = phase.Rules.SetItem(0, phase.Rules[0] with
                { Behavior = [.. phase.Rules[0].Behavior, Set("[slenikon from area]", 0), Set("[slenikon to area]", start),
                    Set("[slenikon speed]", speed), next, setTimer] }).Add(
                new([new("[check timer end]", 0, null, null, null, timer, [timer.Kind, timer.DungeonId])], [next, setTimer])) };
        }
        var clock = new LukeRaidClockTests.Clock();
        var battle = new AntonRaidBattle(source with { Phases = [Flight(source.Phases[0], 301, 15), Flight(source.Phases[1], 317, 10)] },
            kind: RaidKind.Prey, clock: clock, preyGuide: false);
        foreach (int expected in Enumerable.Range(301, 16).Concat([301]))
        {
            That(battle.View.Symbols["[slenikon from area]"] == expected, "一阶段自然航线保持16站顺序循环");
            Step(15);
        }
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[first complete]", 1);
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null, "航线测试进入普通二阶段");
        foreach (int expected in new[] { 317, 320, 318, 320, 319, 320, 317, 320 })
        {
            That(battle.View.Symbols["[slenikon from area]"] == expected &&
                battle.View.Symbols["[slenikon arrival time]"] == clock.GetUtcNow().ToUnixTimeSeconds() + 10,
                "二阶段外围据点之间均经过本体，下一站期限保持本阶段资源步长");
            Step(10);
        }

        void Step(int seconds)
        {
            clock.AdvanceWithoutCallbacks(seconds);
            That(battle.ExpireLukeTimer(battle.Instance, timer, battle.View.TimerGenerations[timer]) is not null,
                "当前代自然航行计时器推进下一站");
        }
    }

    static void AdmissionRules()
    {
        var start = new DungeonMap("map", new(0, 0), 1001, "map/prey-start.map");
        var boss = new DungeonMap("boss", new(1, 0), 1002, "map/prey-boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB EE", [start.Position], [boss.Position], [start, boss], []);
        var definition = new DungeonDefinition(4988, "dungeon/gcontents/190307_prey_raid/sample.dgn", 95, 130, [maze],
            [new("difficulty", [new(0, 2)]), new("raid dungeon", [new(0, 1)])]);
        var character = new Character(1, 0, "PreyAdmission", 0, new(59, 2, 1, 1), null) { Level = 95 };
        That(Enumerable.Range(0, 6).All(value => definition.AllowsDifficulty((byte)value, false) == (value == 2)),
            "普通普雷只接受原DGN的固定难度2，不把请求改0或开放其它档");
        That(!(definition with { Path = "dungeon/other/sample.dgn" }).AllowsDifficulty(2, false) &&
            !(definition with { RawSections = [new("difficulty", [new(0, 2)])] }).AllowsDifficulty(2, false),
            "固定难度解释仅作用实际普雷正式目录与raid声明，不扩大普通副本");
        var instance = Guid.NewGuid();
        for (byte squad = 1; squad <= 4; squad++)
        {
            var guide = new RaidDungeonIdentity(instance, 0, squad, RaidKind.Prey);
            var team = guide with { PreyGuide = false, PreyPartyId = 101 };
            That(guide.PreyGuide && guide != team, "旧令牌默认引导，普通/引导模式属于冻结身份");
            bool guideReady = DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string guideReason,
                raid: guide, raidMaze: 0);
            bool teamReady = DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string teamReason,
                raid: team, raidMaze: 0);
            That(guideReady == (squad == 1) && (guideReady || guideReason == "raid-maze-admission-mismatch"),
                "默认引导令牌仍严格限定第一队");
            That(!teamReady && teamReason == "raid-maze-admission-mismatch",
                "普通令牌不能把全团分队标记当作战斗组身份");
        }
        var ordinary = new RaidDungeonIdentity(instance, 0, 0, RaidKind.Prey) { PreyGuide = false };
        That(!DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string missingParty,
            raid: ordinary, raidMaze: 0) && missingParty == "raid-maze-admission-mismatch",
            "普通令牌缺少实际队伍或单人身份时不能进图");
        var solo = ordinary with { PreySoloCharacter = character.Id };
        That(DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out _, raid: solo, raidMaze: 0),
            "普通单人令牌使用本人身份通过入场规划，无需建立普通Party");
        That(!DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string mixedGroup,
            raid: solo with { PreyPartyId = 101 }, raidMaze: 0) && mixedGroup == "raid-maze-admission-mismatch",
            "普通令牌不能同时声明单人和实际Party身份");
        foreach (ushort party in new ushort[] { 0, 101, 102 })
        {
            var identity = ordinary with { PreyPartyId = party };
            That(DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out _,
                raid: identity, raidMaze: 0),
                "普通令牌按实际Party身份通过入场规划，含有效的零号Party，不依赖全团分队标记");
        }
        That((ordinary with { PreyPartyId = 101 }) != (ordinary with { PreyPartyId = 102 }),
            "不同实际Party属于不同冻结战斗身份");
        That(!DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string invalidParty,
            raid: ordinary with { PreyPartyId = ushort.MaxValue }, raidMaze: 0) && invalidParty == "raid-maze-admission-mismatch" &&
            !DungeonEntryPlan.TryCreate(definition, character, 4988, 0, 2, out _, out string guideParty,
                raid: new(instance, 0, 1, RaidKind.Prey) { PreyPartyId = 101 }, raidMaze: 0) && guideParty == "raid-maze-admission-mismatch",
            "普通拒绝无效Party号，引导令牌不能混入普通Party身份");
    }

    static RaidScript Script()
    {
        string[] names = ["[slenikon hp]", "[slenikon speed]", "[slenikon from area]", "[slenikon to area]", "[slenikon arrival time]",
            "[slenikon invincibility]", "[slenikon armor level]", "[isys hp]", "[isys hp in dungeon]", "[isys groggy]",
            "[prepare raid]", "[merchant]", "[first init]", "[second init]", "[first complete]", "[kick observed]",
            "[hp10]", "[hp11]", "[hp12]", "[hp13]", "[hp14]", "[hp15]", "[hp20]", "[hp21]", "[11 magic tower spawn position]", "[11 magic tower spawn time]"];
        var symbols = names.Concat(Enumerable.Range(1, 20).SelectMany(slot => new[] { $"[area_{slot}]", $"[area_{slot}_dgn_type]" }))
            .Select((name, index) => (name, index)).ToImmutableDictionary(row => row.name, row => row.index);
        RaidClause If(string symbol, int value) => new("[if]", 0, null, symbol, null, null, [value]);
        RaidClause Set(string symbol, int value) => new("[set]", 0, null, symbol, null, null, [value]);
        RaidClause Timer(string tag, int kind, int id, int? seconds = null) => new(tag, 0, null, null, null, new(kind, id),
            seconds is { } duration ? [kind, id, duration] : [kind, id]);
        RaidRule Rule(RaidClause trigger, params RaidClause[] actions) => new([trigger], [.. actions]);
        RaidDungeon Dungeon(int id, string type, int hp = 1) => new(id, RaidDungeonState.Open, $"[hp{id}]", hp, 1)
        {
            Declarations = [new("[type]", 0, [new(6, 0, type)]), new("[rate]", 0, [new(0, 1, null)])]
        };
        RaidClause Clear() => new("[clear phase]", 0, null, null, null, null, []);
        var first = new RaidPhase("first", [Dungeon(10, "slenikon defence", 9999), Dungeon(11, "magic tower"),
            Dungeon(12, "sanctuary"), Dungeon(13, "sanctuary"), Dungeon(14, "sanctuary"), Dungeon(15, "sanctuary")],
        [
            Rule(If("[first init]", 0), Set("[first init]", 1), Set("[slenikon speed]", 15), Set("[slenikon to area]", 301),
                Set("[isys hp]", 10000), Set("[isys hp in dungeon]", 10000)),
            Rule(If("[prepare raid]", 1), new("[reserve spawn]", 0, 11, null, null, null, [11, 180]),
                Timer("[set timer]", 500, 11, 180), Timer("[raid timer]", 1, 150, 30)),
            Rule(Timer("[check timer end]", 500, 11), new("[spawn]", 0, 11, null, null, null, [11]), Set("[merchant]", 3),
                Timer("[raid timer]", 1, 150, 30)),
            Rule(Timer("[check raid timer end]", 1, 150), Set("[merchant]", 0)),
            Rule(If("[first complete]", 1), Clear())
        ], []);
        var second = new RaidPhase("second", [Dungeon(20, "garden", 9999), Dungeon(21, "boss", 9999)],
        [
            Rule(If("[second init]", 0), Set("[second init]", 1), Set("[area_20]", 21), Set("[isys groggy]", 1), Timer("[set timer]", 2, 211, 30)),
            Rule(Timer("[check timer end]", 2, 211),
                new("[assign]", 0, null, "[isys hp]", null, null, []) { Operator = "[isys hp in dungeon]" },
                Set("[isys groggy]", 0), new("[kick out dungeon]", 0, 21, null, null, null, [21])),
            Rule(new("[on kick out dungeon]", 0, 21, null, null, null, [21]), Set("[kick observed]", 1),
                new("[set dungeon state]", 0, 20, null, RaidDungeonState.Close, null, [20])),
            Rule(If("[isys hp]", 0), Clear())
        ], []);
        RaidField Header(string tag, params int[] values) => new(tag, 0, values.Select(value => new RaidValue(0, value, null)).ToImmutableArray());
        return new("fixture/prey-team.etc", new(59, 2), new(1, 1), new(1, 1), symbols, [first, second],
            [Header("[start delay time]", 15), Header("[max hp]", 10000), Header("[hp decrease]", 2, 17),
                Header("[defence fail]", 1300), Header("[armor level hp boundary]", 6000, 2000)], []);
    }
}
