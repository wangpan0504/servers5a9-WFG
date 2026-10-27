using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidEntryTests
{
    // 借用开战用例已提交的20人实例，不另起数据库或复制在线状态。
    internal static void RunCore(RaidRegistry registry, PartyService parties, RaidActor[] actors, RaidScript script)
    {
        var maze = new DungeonMaze(0, 1, 1, "BBEE", [new(0, 0)], [new(0, 0)], [], []);
        var definitions = ImmutableDictionary<int, DungeonDefinition>.Empty.Add(210,
            new(210, "dungeon/anton/fixture.dgn", 1, 1, [maze], []));
        var catalog = new RaidDungeonCatalog(script, definitions, [], new Dictionary<int, string>(), _ => true,
            dungeonPaths: ImmutableDictionary<int, string>.Empty.Add(210, "dungeon/anton/fixture.dgn"));
        var leader = actors[0].Session;
        var party = parties.Current(leader)!;
        That(!new RaidStartSettings().AntonFormalEntryEnabled && !new RaidStartSettings().AntonPhaseProgressEnabled,
            "安徒恩正式进图和阶段推进配置都缺省关闭");
        var disabled = new AntonRaidDungeonService(registry, parties, catalog, false);
        That(disabled.Select(leader, 210, party, out var reason).Length == 0 && reason == "anton-entry-waiting-client-resources" &&
            leader.CurrentDungeonSelection is null, "关闭时不安装授权，保留原战况表行为");
        var missing = new RaidDungeonCatalog(script, ImmutableDictionary<int, DungeonDefinition>.Empty, [32200],
            new Dictionary<int, string>(), _ => false);
        That(new AntonRaidDungeonService(registry, parties, missing, true).Select(leader, 210, party, out reason).Length == 0 &&
            reason == "anton-entry-missing-maps", "开关不能绕过MAP缺项门禁");
        var service = new AntonRaidDungeonService(registry, parties, catalog, true);
        foreach (int id in new[] { 211, 218, int.MaxValue })
            That(service.Select(leader, id, party, out reason).Length == 0 && reason == "anton-entry-dungeon-not-open",
                "首阶段只接受实际Open的210，隐藏/跨阶段/未知DGN拒绝");
        That(service.Select(actors[1].Session, 210, party, out _).Length == 0 &&
            service.Select(leader, 210, party with { }, out _).Length == 0 &&
            service.Select(actors[20].Session, 210, party, out _).Length == 0,
            "非分队长、非当前Party引用及团外角色不能签发授权");
        var selected = service.Select(leader, 210, party, out reason);
        That(selected.Length == 4 && reason is null && party.Members.All(member => member.Actor.Session.HasFormalRaidSelection),
            "正常开战后的原分队整体取得同Battle授权");
        var battle = leader.CurrentRaidBattle(actors[0])!;
        var admission = leader.CurrentDungeonSelection!.Raid!.Admission;
        That(admission.Allows(210) && !admission.Allows(211) &&
            !(admission with { InstanceId = Guid.NewGuid() }).Current && !(admission with { Phase = 1 }).Current,
            "授权锁定原实例和合法阶段，不由复制令牌改实例或阶段");
        var first = Bind(actors[..4]);
        var identity = new RaidDungeonIdentity(battle.Instance, 0, 1);
        using (var pending = battle.ReserveDungeon(identity, 210, first))
        {
            That(pending is not null && battle.ReserveDungeon(identity, 210, Bind(actors[..4])) is null,
                "同分队只能预留一个Run，未提交Dispose撤销本次占用");
        }
        using (var pending = battle.ReserveDungeon(identity, 210, first)!) pending.Install();
        That(battle.CompleteDungeon(new(first[0].Identity, 210, [Guid.NewGuid()])
            { ClearMap = new("boss", new(0, 0), 1, "fixture.map") }) is null && battle.View.Revision == 0,
            "入口绑定不授予旧单人清图阶段推进能力");
        var second = Bind(actors[4..8]);
        using (var parallel = battle.ReserveDungeon(identity with { Squad = 2 }, 210, second))
            That(parallel is not null, "黑雾资源允许多分队独立Run，仍分别持有原分队");
        foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        var next = Bind(actors[..4]);
        using (var pending = battle.ReserveDungeon(identity, 210, next)!) pending.Install();
        foreach (var row in first) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        That(battle.ReserveDungeon(identity, 210, Bind(actors[..4])) is null, "旧Run迟到退出不能释放新Run占用");
        foreach (var row in next) battle.ReleaseDungeon(row.Actor, row.Identity.InstanceId);
        leader.CancelRaidDungeonSelection(selected);
        That(party.Members.All(member => member.Actor.Session.CurrentDungeonSelection?.Raid is null),
            "取消只清本次授权，保留可继续使用的MapSelect上下文");
        foreach (var member in party.Members) member.Actor.Session.ReturnFromDungeonSelection(restorePosition: false);
        That(service.Select(leader, 210, party, out _).Length == 4, "释放和取消后原分队可以重新选择");
        var replacement = leader.CurrentDungeonSelection;
        leader.CancelRaidDungeonSelection(selected);
        That(ReferenceEquals(replacement, leader.CurrentDungeonSelection), "旧选择清理不能覆盖新选择");
        var latest = party.Members.Select(member => new PartyDungeonNotice(PartyDungeonNoticeKind.RaidSelection, member, party.Members,
            Selection: new(member.Actor.Session.Selected!, member.Actor.Owner.Generation, member.Actor.Epoch,
                member.Actor.Session.CurrentDungeonSelection!.Id))).ToArray();
        leader.CancelRaidDungeonSelection(latest);
        foreach (var member in party.Members) member.Actor.Session.ReturnFromDungeonSelection(restorePosition: false);
        PhaseProgress(actors[0], actors[1], actors[2]);
        PhaseZeroRemaining(actors);

        static (RaidActor Actor, DungeonRunIdentity Identity)[] Bind(RaidActor[] group)
        {
            Guid run = Guid.NewGuid();
            return group.Select(actor => (actor, new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
        }
    }

    // 合成规则只复现已证的“210 clear → 改开放状态并留下计时声明”。不把 300 解释成秒。
    static void PhaseProgress(RaidActor first, RaidActor second, RaidActor third)
    {
        var script = new RaidScript("synthetic/anton-phase.etc", new(19, 2), null, null,
            ImmutableDictionary<string, int>.Empty.Add("[hp]", 1).Add("[dgn smoke open]", 2),
            [new RaidPhase("phase0",
                [new(210, RaidDungeonState.Open, "[hp]", 4, 5), new(212, RaidDungeonState.Hide, "[hp]", 1, 5)],
                [new RaidRule(
                    [new("[if]", 0, null, "[dgn smoke open]", null, null, [0]),
                     new("[check dungeon state]", 1, 210, null, RaidDungeonState.Clear, null, [])],
                    [new("[set dungeon state]", 2, 210, null, RaidDungeonState.Hide, null, []),
                     new("[set dungeon state]", 3, 212, null, RaidDungeonState.Open, null, []),
                     new("[set]", 4, null, "[dgn smoke open]", null, null, [1]),
                     new("[set timer]", 5, 211, null, null, new RaidTimer(2, 211), [2, 211, 300]),
                     new("[reserve dungeon state]", 6, 216, null, RaidDungeonState.Open, null, [216, 0, 20])])], [])], [], []);
        var map = new DungeonMap("boss", new(0, 0), 1, "fixture.map");
        var locked = new AntonRaidBattle(script);
        That(locked.JoinStart(first) && !locked.PhaseProgress, "缺省战斗聚合不打开阶段推进");
        var lockedIdentity = new DungeonRunIdentity(first.Character, 0, 1, Guid.NewGuid(), 1);
        using (var pending = locked.ReserveDungeon(new(locked.Instance, 0, 1), 210, [(first, lockedIdentity)])!) pending.Install();
        That(locked.CompleteAntonReservedDungeon(new(lockedIdentity, 210, [Guid.NewGuid()]) { ClearMap = map }) is null &&
            locked.View.Revision == 0 && locked.View.Dungeons[210] == RaidDungeonState.Open && !locked.AllowsAntonEntry(212),
            "开关关闭时已安装 Run 的清图不改战况，下一图保持不可进");

        var opened = new AntonRaidBattle(script, antonPhaseProgress: true);
        That(opened.JoinStart(first) && opened.JoinStart(second) && opened.JoinStart(third) && opened.PhaseProgress, "显式开关只作用于本次聚合");
        var identity = new DungeonRunIdentity(first.Character, 0, 1, Guid.NewGuid(), 1);
        using (var pending = opened.ReserveDungeon(new(opened.Instance, 0, 1), 210, [(first, identity)])!) pending.Install();
        var later = identity with { Epoch = identity.Epoch + 1 };
        var change = opened.CompleteAntonReservedDungeon(new(later, 210, [Guid.NewGuid()]) { ClearMap = map });
        That(change is not null && opened.View.Dungeons[210] == RaidDungeonState.Hide && opened.View.Dungeons[212] == RaidDungeonState.Open &&
            opened.View.Symbols["[dgn smoke open]"] == 1 && opened.View.Timers[new RaidTimer(2, 211)] == 300 &&
            opened.View.Deferred.Values.Any(clause => clause.Tag == "[reserve dungeon state]" && clause.DungeonId == 216 && clause.RawIntegers[2] == 20) &&
            opened.View.State == AntonRaidBattleState.Fighting && opened.AllowsAntonEntry(212) && !opened.AllowsAntonEntry(210),
            "210 清图按规则隐藏自身、开放 212，计时和延迟开图只保留原值");
        That(opened.CompleteAntonReservedDungeon(new(later, 210, [Guid.NewGuid()]) { ClearMap = map }) is null &&
            opened.ExpireTimer(opened.Instance, opened.View.Revision, new RaidTimer(2, 211)) is null,
            "重复清图和未证时间单位都不能再次推进或触发计时行为");
        var next = new DungeonRunIdentity(second.Character, 0, 1, Guid.NewGuid(), 1);
        using var follow = opened.ReserveDungeon(new(opened.Instance, 0, 2), 212, [(second, next)]);
        That(follow is not null && opened.ReserveDungeon(new(opened.Instance, 0, 3), 210, [(third, new DungeonRunIdentity(third.Character, 0, 1, Guid.NewGuid(), 1))]) is null,
            "其它分队可以进入新开放图，已隐藏的 210 不能再预留");
    }

    // 只复现 anton.etc 阶段 0 里不依赖计时到期的 clear 声明。300/150/20 仍是原值。
    static void PhaseZeroRemaining(RaidActor[] actors)
    {
        var symbols = ImmutableDictionary<string, int>.Empty
            .Add("[dgn smoke open]", 120).Add("[movie smoke out]", 122).Add("[1phase success]", 105)
            .Add("[smoke passive create]", 1).Add("[movie navigun success]", 124).Add("[meteo passive create]", 2);
        RaidDungeon Row(int id, RaidDungeonState state) => new(id, state, "[hp]", 1, id == 216 ? 1 : 5);
        var phase0 = new[] { 210, 211, 212, 213, 214, 215, 216 }.Select(id => Row(id, id == 210 ? RaidDungeonState.Open : RaidDungeonState.Hide)).ToArray();
        var script = new RaidScript("synthetic/anton-phase0-clear.etc", new(19, 2), null, null, symbols,
            [new RaidPhase("phase0", phase0.ToImmutableArray(), PhaseZeroRules(), []),
             new RaidPhase("phase1", [Row(218, RaidDungeonState.Open)], [], [])], [], []);
        var locked = new AntonRaidBattle(script);
        That(locked.JoinStart(actors[0]), "关闭开关的聚合仍可开战");
        var lockedIdentity = new DungeonRunIdentity(actors[0].Character, 0, 1, Guid.NewGuid(), 1);
        using (var pending = locked.ReserveDungeon(new(locked.Instance, 0, 1), 210, [(actors[0], lockedIdentity)])!) pending.Install();
        That(locked.CompleteAntonReservedDungeon(new(lockedIdentity, 210, [Guid.NewGuid()]) { ClearMap = new("boss", new(0, 0), 1, "fixture-locked.map") }) is null &&
            locked.View.Dungeons[210] == RaidDungeonState.Open && locked.View.Revision == 0 && locked.View.Symbols["[1phase success]"] == 0,
            "开关关闭时 210 清图不改战况，也不记录阶段成功");

        var battle = new AntonRaidBattle(script, antonPhaseProgress: true);
        for (int i = 0; i < 5; i++) That(battle.JoinStart(actors[i]), "阶段推进聚合接收原成员且没有奖励策略");
        Clear(battle, actors[0], 1, 210);
        That(battle.View.State == AntonRaidBattleState.Fighting && battle.View.Phase == 0 &&
            battle.View.Dungeons[210] == RaidDungeonState.Hide && battle.View.Dungeons[211] == RaidDungeonState.Close &&
            new[] { 212, 213, 214, 215 }.All(id => battle.View.Dungeons[id] == RaidDungeonState.Open) &&
            battle.View.Dungeons[216] == RaidDungeonState.Hide &&
            battle.View.Symbols["[dgn smoke open]"] == 1 && battle.View.Symbols["[movie smoke out]"] == 1 &&
            battle.View.Symbols["[1phase success]"] == 0 &&
            battle.View.Timers[new RaidTimer(2, 211)] == 300 && battle.View.Timers[new RaidTimer(1, 212)] == 300 &&
            battle.View.Timers[new RaidTimer(1, 214)] == 300 &&
            Deferred(battle, 216, 20) && !battle.AllowsAntonEntry(211) && !battle.AllowsAntonEntry(216) &&
            new[] { 212, 213, 214, 215 }.All(battle.AllowsAntonEntry),
            "210 clear 只发布已闭合的战况；211 与 216 仍不能进，计时原值不调度");
        That(battle.ExpireTimer(battle.Instance, battle.View.Revision, new RaidTimer(2, 211)) is null &&
            battle.ExpireTimer(battle.Instance, battle.View.Revision, new RaidTimer(1, 212)) is null,
            "未证单位的阶段 0 计时不能被当成到期事实");
        Clear(battle, actors[1], 2, 212);
        That(battle.View.Dungeons[212] == RaidDungeonState.Close && !battle.View.Timers.ContainsKey(new RaidTimer(1, 212)) &&
            battle.View.Timers[new RaidTimer(2, 212)] == 150 && Deferred(battle, 212, 150) &&
            battle.View.State == AntonRaidBattleState.Fighting && !battle.AllowsAntonEntry(212),
            "212 clear 关闭自身并留下 150 的延迟重开原值");
        Clear(battle, actors[2], 3, 214);
        That(battle.View.Dungeons[214] == RaidDungeonState.Close && battle.View.Timers[new RaidTimer(2, 214)] == 150 &&
            Deferred(battle, 214, 150) && battle.View.Dungeons[213] == RaidDungeonState.Open,
            "214 clear 与 212 同一类关闭和延迟声明");
        Clear(battle, actors[3], 4, 213);
        That(battle.View.State == AntonRaidBattleState.Fighting && battle.View.Dungeons[213] == RaidDungeonState.Clear &&
            battle.View.Dungeons[215] == RaidDungeonState.Open && battle.View.Symbols["[1phase success]"] == 0,
            "只有一条腿 clear 不记录阶段成功");
        Clear(battle, actors[4], 5, 215);
        That(battle.View.State == AntonRaidBattleState.Reward && battle.View.Phase == 0 &&
            battle.View.Symbols["[1phase success]"] == 1 &&
            new[] { 210, 211, 212, 213, 214, 215, 216 }.All(id => battle.View.Dungeons[id] == RaidDungeonState.Hide) &&
            battle.View.Timers.IsEmpty && !new[] { 210, 211, 212, 213, 214, 215, 216, 218 }.Any(battle.AllowsAntonEntry),
            "213 与 215 都 clear 后隐藏阶段 0 并进入 Reward，不开放阶段 1");
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is null && battle.View.Phase == 0 &&
            battle.RewardPolicy(actors[0], 0) is null,
            "没有奖励策略时阶段成功不能进入阶段 1");

        var side = new AntonRaidBattle(new RaidScript("synthetic/anton-phase0-side.etc", new(19, 2), null, null, symbols,
            [new RaidPhase("phase0", [Row(211, RaidDungeonState.Open), Row(216, RaidDungeonState.Open)],
                [Rule([On(211), Check(211, RaidDungeonState.Clear)],
                    [State(211, RaidDungeonState.Close), Timer(2, 211, 300), Reset(1, 211), Reset(3, 211),
                     Set("[smoke passive create]", 0)]),
                 Rule([On(216), Check(216, RaidDungeonState.Clear)],
                    [State(216, RaidDungeonState.Close), Set("[movie navigun success]", 1), Set("[meteo passive create]", 0),
                     Reset(1, 216), Timer(2, 216, 150)])], [])], [], []), antonPhaseProgress: true);
        That(side.JoinStart(actors[5]) && side.JoinStart(actors[6]), "旁路聚合只验证已闭合的 clear 声明");
        Clear(side, actors[5], 1, 211);
        That(side.View.Dungeons[211] == RaidDungeonState.Close && side.View.Timers[new RaidTimer(2, 211)] == 300 &&
            side.View.Symbols["[smoke passive create]"] == 0 && side.View.State == AntonRaidBattleState.Fighting,
            "211 一旦 clear 就关闭并留下计时原值；生产路径仍要等未证的开放延迟");
        Clear(side, actors[6], 2, 216);
        That(side.View.Dungeons[216] == RaidDungeonState.Close && side.View.Timers[new RaidTimer(2, 216)] == 150 &&
            side.View.Symbols["[movie navigun success]"] == 1 && side.View.Symbols["[meteo passive create]"] == 0,
            "216 clear 关闭自身并留下成功符号原值；生产路径的 20 延迟仍未调度");

        static void Clear(AntonRaidBattle battle, RaidActor actor, byte squad, int dungeon)
        {
            var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
            using (var pending = battle.ReserveDungeon(new(battle.Instance, 0, squad), checked((uint)dungeon), [(actor, identity)])!) pending.Install();
            var fact = new DungeonClearFact(identity with { Epoch = identity.Epoch + 1 }, checked((uint)dungeon), [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 1, "fixture-clear.map") };
            That(battle.CompleteAntonReservedDungeon(fact) is not null, "已安装 Run 的清图进入阶段规则");
        }
        static bool Deferred(AntonRaidBattle battle, int dungeon, int delay) => battle.View.Deferred.Values.Any(clause =>
            clause.Tag == "[reserve dungeon state]" && clause.DungeonId == dungeon && clause.State == RaidDungeonState.Open &&
            clause.RawIntegers[^1] == delay);
    }

    static ImmutableArray<RaidRule> PhaseZeroRules() =>
    [
        Rule([If("[dgn smoke open]", 0), Check(210, RaidDungeonState.Clear)],
            [State(210, RaidDungeonState.Hide), State(211, RaidDungeonState.Close), State(212, RaidDungeonState.Open),
             State(213, RaidDungeonState.Open), State(214, RaidDungeonState.Open), State(215, RaidDungeonState.Open),
             Reserve(216, 20), Set("[dgn smoke open]", 1), Set("[movie smoke out]", 1), Timer(2, 211, 300)]),
        Rule([On(212), Check(212, RaidDungeonState.Open)], [Timer(1, 212, 300), Reset(2, 212)]),
        Rule([On(214), Check(214, RaidDungeonState.Open)], [Timer(1, 214, 300), Reset(2, 214)]),
        Rule([On(212), Check(212, RaidDungeonState.Clear)],
            [State(212, RaidDungeonState.Close), Reserve(212, 150), Reset(1, 212), Timer(2, 212, 150)]),
        Rule([On(214), Check(214, RaidDungeonState.Clear)],
            [State(214, RaidDungeonState.Close), Reserve(214, 150), Reset(1, 214), Timer(2, 214, 150)]),
        Rule([On(213), Check(213, RaidDungeonState.Clear), Check(215, RaidDungeonState.Clear)], [Set("[1phase success]", 1)]),
        Rule([On(215), Check(213, RaidDungeonState.Clear), Check(215, RaidDungeonState.Clear)], [Set("[1phase success]", 1)]),
        Rule([OnSymbol("[1phase success]"), If("[1phase success]", 1)],
            [State(210, RaidDungeonState.Hide), State(211, RaidDungeonState.Hide), State(212, RaidDungeonState.Hide),
             State(213, RaidDungeonState.Hide), State(214, RaidDungeonState.Hide), State(215, RaidDungeonState.Hide),
             State(216, RaidDungeonState.Hide), new("[clear phase]", 90, null, null, null, null, [])])
    ];

    static RaidRule Rule(RaidClause[] trigger, RaidClause[] behavior) => new(trigger.ToImmutableArray(), behavior.ToImmutableArray());
    static RaidClause If(string symbol, int value) => new("[if]", 1, null, symbol, null, null, [value]);
    static RaidClause On(int dungeon) => new("[on change dungeon state]", 2, dungeon, null, null, null, []);
    static RaidClause OnSymbol(string symbol) => new("[on change symbol]", 3, null, symbol, null, null, []);
    static RaidClause Check(int dungeon, RaidDungeonState state) => new("[check dungeon state]", 4, dungeon, null, state, null, []);
    static RaidClause State(int dungeon, RaidDungeonState state) => new("[set dungeon state]", 5, dungeon, null, state, null, []);
    static RaidClause Set(string symbol, int value) => new("[set]", 6, null, symbol, null, null, [value]);
    static RaidClause Timer(int kind, int dungeon, int value) => new("[set timer]", 7, dungeon, null, null, new RaidTimer(kind, dungeon), [kind, dungeon, value]);
    static RaidClause Reset(int kind, int dungeon) => new("[reset timer]", 8, dungeon, null, null, new RaidTimer(kind, dungeon), []);
    static RaidClause Reserve(int dungeon, int delay) => new("[reserve dungeon state]", 9, dungeon, null, RaidDungeonState.Open, null, [dungeon, 0, delay]);
}
