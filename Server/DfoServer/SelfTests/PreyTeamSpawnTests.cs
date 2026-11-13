using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamSpawnTests
{
    internal static void Run(RaidScript script)
    {
        int[] towers = script.Phases[0].Rules.Where(rule => rule.Trigger.Any(clause =>
            clause.Tag == "[if]" && clause.Symbol == "[prepare raid]" && clause.RawIntegers is [1]))
            .SelectMany(rule => rule.Behavior).Where(action => action.Tag == "[reserve spawn]")
            .Select(action => action.DungeonId!.Value).Distinct().ToArray();
        That(towers.Length == 2, "随机预告回归使用原准备规则中的两座魔塔");
        int occupied = script.Phases[0].Dungeons.First(row => row.Declarations.Any(field =>
            field.Tag == "[type]" && field.Values is [{ Text: "sanctuary" }])).Id;
        int[] reachable = [302, 306, 314];
        foreach (int choice in Enumerable.Range(0, reachable.Length))
        {
            var (battle, random, clock) = Create(choice, [2, 6, 10, 14]);
            Expire(battle, new(9000, 0));
            int announced = Position(battle, towers[0]);
            That(announced == reachable[choice] && Position(battle, towers[1]) != announced,
                "同一空位集合的各合法格均可被抽中，不选已占格或另一塔原预留310");
            int deadline = battle.View.Symbols[$"[{towers[0]} magic tower spawn time]"];
            var timer = new RaidTimer(500, towers[0]);
            clock.AdvanceWithoutCallbacks(battle.View.Timers[timer]);
            random.Choice = reachable.Length - choice - 1;
            That(Position(battle, towers[0]) == announced && battle.View.Symbols[$"[{towers[0]} magic tower spawn time]"] == deadline,
                "已公布预告的位置和期限保持到原生成事件，不随随机源变化重新抽选");
            Expire(battle, timer);
            That(battle.View.Symbols[$"[area_{announced - 300}]"] != 0 &&
                battle.View.Symbols[$"[area_{announced - 300}_dgn_type]"] == 5,
                "原定时事件优先在本塔已公布且仍合法的位置生成真实阻击");
        }

        var (fallback, fallbackRandom, _) = Create(0, [2, 6, 10, 14]);
        Expire(fallback, new(9000, 0));
        int invalidated = Position(fallback, towers[0]);
        int otherReserved = Position(fallback, towers[1]);
        fallback.SetMechanism(fallback.Instance, fallback.View.Revision, $"[area_{invalidated - 300}]", occupied);
        fallbackRandom.Choice = 1;
        Expire(fallback, new(500, towers[0]));
        That(fallback.View.Symbols["[area_14_dgn_type]"] == 5 &&
            fallback.View.Symbols[$"[area_{invalidated - 300}]"] == occupied && Position(fallback, towers[1]) == otherReserved,
            "失效预告从同一合法空位集合随机回退，可选到末格，保留已占格及另一塔预留");

        var (full, fullRandom, _) = Create(0, []);
        int calls = fullRandom.Calls;
        Expire(full, new(9000, 0));
        That(towers.All(tower => Position(full, tower) == 0) && fullRandom.Calls == calls,
            "没有合法空格时预告位置为0，不调用空集合随机抽选");
        Expire(full, new(500, towers[0]));
        That(Enumerable.Range(1, 16).All(slot => full.View.Symbols[$"[area_{slot}]"] == occupied) && fullRandom.Calls == calls,
            "无空位的生成事件安全结束，不覆盖已有据点或消耗随机数");

        (AntonRaidBattle Battle, ChoiceRandom Random, LukeRaidClockTests.Clock Clock) Create(int choice, int[] free)
        {
            var random = new ChoiceRandom();
            var clock = new LukeRaidClockTests.Clock();
            var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, clock: clock, preyRandom: random, preyGuide: false);
            // 显式构造同一合法空位集合；不把这些合成占用当成正式地图、通关或贡献事实。
            foreach (int slot in Enumerable.Range(1, 16))
                battle.SetMechanism(battle.Instance, battle.View.Revision, $"[area_{slot}]", free.Contains(slot) ? 0 : occupied);
            battle.SetMechanism(battle.Instance, battle.View.Revision, $"[{towers[1]} magic tower spawn position]", 310);
            random.Choice = choice;
            return (battle, random, clock);
        }
    }

    static int Position(AntonRaidBattle battle, int tower) => battle.View.Symbols[$"[{tower} magic tower spawn position]"];

    static void Expire(AntonRaidBattle battle, RaidTimer timer) =>
        That(battle.ExpireLukeTimer(battle.Instance, timer, battle.View.TimerGenerations[timer]) is not null,
            "随机预告仅消费当前原代计时事件");

    sealed class ChoiceRandom : Random
    {
        internal int Choice, Calls;
        public override int Next(int maxValue) { Calls++; return Math.Min(Choice, maxValue - 1); }
    }
}
