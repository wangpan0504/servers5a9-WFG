using System.Collections.Immutable;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamLandingTests
{
    internal static void RunCore(RaidScript source)
    {
        RaidClause Timer(string tag) => new(tag, 0, null, null, null, new(1, 1000), tag == "[set timer]" ? [1, 1000, 15] : [1, 1000]);
        var move = new RaidClause("[slenikon]", 0, null, null, null, null, []);
        var first = source.Phases[0] with { Rules = [.. source.Phases[0].Rules,
            new RaidRule([new("[if]", 0, null, "[prepare raid]", null, null, [1])], [Timer("[set timer]"), move]),
            new RaidRule([Timer("[check timer end]")], [Timer("[set timer]"), move])] };
        var script = source with { Phases = source.Phases.SetItem(0, first) };
        var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyRandom: new Random(7), preyGuide: false);
        That(battle.CapturePreyLandingArrival(battle.View) is null, "普雷准备期不签发到达候选");
        Expire(new(9000, 0));
        var view = battle.View;
        var firstLanding = battle.CapturePreyLanding(view, 4);
        That(firstLanding is { Dungeon: 12, Side: 4, Arrival.FromArea: 301, Arrival.Phase: 0 } &&
            battle.CapturePreyLanding(view, 5) is null && battle.CapturePreyLanding(view, 3) is null,
            "当前到达圣地只安排左侧，中央及防御侧不授权");
        Expire(new(9001, 0));
        That(battle.IsPreyLandingCurrent(firstLanding!) && battle.CapturePreyLanding(view, 4) == firstLanding,
            "无关船体HP更新不使同次到达身份失效");
        Expire(new(1, 1000));
        That(!battle.IsPreyLandingCurrent(firstLanding!) && battle.CapturePreyLanding(view, 4) is null,
            "离站后旧generation及from候选均不能用于入图");
        That(battle.AllowsPreyTeamEntry(10), "初始阻击不等待魔塔计时即可从右侧进入");
        // 显式腾空初始阻击，只为继续覆盖魔塔后续生成和空AREA准入分支。
        PreyTeamBattleTests.RemoveDefenceAreas(battle, script);
        That(!battle.AllowsPreyTeamEntry(10) &&
            !Enumerable.Range(1, 16).Any(slot => battle.View.Symbols.GetValueOrDefault($"[area_{slot}]") == 10),
            "已清空的阻击不可入且不占区域，不因领域原型Open而继续开放");
        Expire(new(500, 11));
        int defence = Enumerable.Range(1, 16).Single(slot => battle.View.Symbols[$"[area_{slot}]"] == 10);
        for (int step = 0; step < 16 && battle.View.Symbols["[slenikon from area]"] != 300 + defence; step++)
            Expire(new(1, 1000));
        var right = battle.CapturePreyLanding(battle.View, 5);
        That(right is { Dungeon: 10, Side: 5 } && battle.CapturePreyLanding(battle.View, 4) is null,
            "当前防御据点只安排右侧，沿资源TYPE判定而非矩形坐标");
        That(!battle.IsPreyLandingCurrent(right! with { Arrival = right!.Arrival with { Phase = 1 } }) &&
            new AntonRaidBattle(script, kind: RaidKind.Prey).CapturePreyLandingArrival(view) is null,
            "旧阶段及其他实例不能复用到达能力");
        foreach (var dungeon in source.Phases[0].Dungeons)
        {
            byte side = dungeon.Declarations.Any(row => row.Tag == "[type]" && row.Values is [{ Text: "slenikon defence" }]) ? (byte)5 : (byte)4;
            That(battle.AllowsPreyEntryFromSide(dungeon.Id, side) &&
                !battle.AllowsPreyEntryFromSide(dungeon.Id, side == 4 ? (byte)5 : (byte)4),
                "一阶段阻击右侧、圣地及魔塔左侧，反向不能手工绕过");
        }
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[first complete]", 1);
        That(battle.AdvanceAfterRewards(battle.Instance, battle.View.Revision) is not null && battle.View.Phase == 1,
            "左右入口回归沿真实阶段结束进入二阶段");
        foreach (var dungeon in source.Phases[1].Dungeons)
        {
            byte side = dungeon.Declarations.Any(row => row.Tag == "[type]" && row.Values is [{ Text: "boss" }]) ? (byte)5 : (byte)4;
            That(battle.AllowsPreyEntryFromSide(dungeon.Id, side) &&
                !battle.AllowsPreyEntryFromSide(dungeon.Id, side == 4 ? (byte)5 : (byte)4),
                "二阶段本体从右侧进入，其余图从左侧进入，反向请求明确拒绝");
        }

        void Expire(RaidTimer timer) => That(battle.ExpireLukeTimer(battle.Instance, timer,
            battle.View.TimerGenerations[timer]) is not null, "资源原代移动计时器执行");
    }
}
