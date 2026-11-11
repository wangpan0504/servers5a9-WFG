using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class VariousBuffAssociationTests
{
    internal static VariousBuffDefinition Definition(int id, string group = "association", string lifetime = "expire_time", int count = 600,
        bool save = true) => new(id, group, [VariousBuffCatalog.ReadEffect("epic_rarity_item_drop_rate", [100])], lifetime, [count, 0],
            VariousBuffDungeonScope.All, []) { Save = save, Overlap = true };

    internal static VariousBuffBinding Binding(int item, IEnumerable<VariousBuffDefinition> definitions, bool random = false)
    {
        var rows = definitions.ToArray();
        var choices = random ? rows.Select((row, index) => new VariousBuffChoice([row.BuffId], index == 0 ? 60 : 40)).ToImmutableArray() :
            ImmutableArray.Create(new VariousBuffChoice(rows.Select(row => row.BuffId).ToImmutableArray(), 100));
        return new(new(item, choices, random, []), rows.ToImmutableDictionary(row => row.BuffId));
    }

    public static void RunCore()
    {
        int before = Count;
        var definitions = new[] { Definition(90001), Definition(90002), Definition(90003, "other") }.ToImmutableDictionary(row => row.BuffId);
        PvfTokenSection Row(int item, string field, params int[] values) => new("index", [new(0, item)],
            [new(field, values.Select(value => new PvfToken(0, value)).ToImmutableArray(), [])], true);
        var fixedRow = Row(80001, "buff list", 90001, 90003);
        var randomRow = Row(80002, "probability buff list", 90001, 60, 90002, 30, 90003, 10);
        var associations = VariousBuffCatalog.ReadAssociations([fixedRow, randomRow], definitions);
        int draws = 0;
        That(associations[80001].Select(_ => { draws++; return 0; }).SequenceEqual(new[] { 90001, 90003 }) && draws == 0,
            "固定多Buff关联按原顺序整批选择，不额外消耗随机数");
        foreach (var (roll, id) in new[] { (0, 90001), (59, 90001), (60, 90002), (89, 90002), (90, 90003), (99, 90003) })
            That(associations[80002].Select(bound => { That(bound == 100, "概率Buff使用明确的百分比分母"); return roll; }).SequenceEqual(new[] { id }),
                "概率关联保留60/30/10的原临界与互斥结果");
        That(VariousBuffCatalog.ReadAssociations([fixedRow, fixedRow], definitions)[80001].Valid &&
            !VariousBuffCatalog.ReadAssociations([fixedRow, Row(80001, "buff list", 90002)], definitions)[80001].Valid,
            "相同重复关联幂等装配，冲突关联保留错误而不是任选一份");
        var unknown = Definition(90004) with { Effects = [VariousBuffCatalog.ReadEffect("future_server_effect", [100, 200])],
            UnsupportedReasons = ["unverified-effect:future_server_effect"] };
        var unready = Binding(80003, [unknown]);
        That(unready.Association.Valid && unknown.BindingReady && !unready.Ready,
            "STK关联和生命周期状态独立于effect执行支持，新增name无需再补编号映射");
        var at = DateTimeOffset.FromUnixTimeSeconds(1900000000);
        var random = new VariousBuffBinding(associations[80002], definitions);
        var grant = VariousBuffPolicy.Grant([], [], [], random, 600000, 7, at, _ => 60);
        That(grant.Saved is [{ BuffId: 90002, UntilMilliseconds: 1900000600000 }] && grant.Transient.IsEmpty &&
            grant.Granted.SequenceEqual(new[] { 90002 }), "抽选结果保存真实Buff身份和截止，不借冷却字段或重登重抽");
        var timed = Definition(90010, count: 10);
        long now = at.ToUnixTimeMilliseconds();
        VariousBuffState Timed(VariousBuffDefinition definition, int? milliseconds) =>
            VariousBuffPolicy.Grant([], [], [], Binding(80006, [definition]), milliseconds, 7, at, _ => 0).Saved.Single();
        That(Timed(timed, 600000).UntilMilliseconds == now + 600000,
            "STK十分钟优先于Buff模板十秒，不混淆毫秒和秒");
        That(Timed(timed, null).UntilMilliseconds == now + 10000,
            "STK未声明时间时回落到Buff模板秒数");
        That(Timed(timed with { ApplyParameters = [] }, null).UntilMilliseconds == now + 1800000,
            "STK和Buff模板均未声明时间时兜底三十分钟");
        That(Timed(timed, 0).UntilMilliseconds == 0 && Timed(timed with { ApplyParameters = [0, 0] }, null).UntilMilliseconds == 0,
            "STK或Buff显式永久0不是未声明，不被缺省时长覆盖");
        PvfTokenSection DurationRow(int id, PvfTokenSection? apply) => new("index", [new(0, id)],
            new[] { new PvfTokenSection("effect", [], [new("name", [new(6, 1)], []), new("param", [new(0, 100)], [])], true) }
                .Concat(apply is null ? [] : new[] { apply }).ToImmutableArray(), true);
        PvfTokenSection Lifetime(int type, PvfTokenSection? parameter) => new("apply type", [],
            new[] { new PvfTokenSection("type", [new(6, type)], []) }
                .Concat(parameter is null ? [] : new[] { parameter }).ToImmutableArray(), true);
        var missingTimes = VariousBuffCatalog.ReadDefinitions([
            DurationRow(90011, null), DurationRow(90012, Lifetime(2, null)),
            DurationRow(90013, Lifetime(2, new("param", [], []))),
            DurationRow(90014, Lifetime(2, new("param", [new(0, -1), new(0, 0)], []))),
            DurationRow(90015, Lifetime(3, null))],
            id => id switch { 1 => "epic_rarity_item_drop_rate", 2 => "expire_time", _ => "dungeon_one_match" });
        That(missingTimes[90011] is { Supported: true, Timed: true, ApplyParameters.IsEmpty: true } &&
            missingTimes[90012] is { Supported: true, ApplyParameters.IsEmpty: true },
            "缺生命周期或缺计时参数保留未声明状态，允许时长按STK或三十分钟解析");
        That(!missingTimes[90013].Supported && !missingTimes[90014].Supported && !missingTimes[90015].Supported,
            "畸形或负计时以及缺失次数不能借缺省时间变成合法资格");
        var mixed = Binding(80004, [Definition(90001, "a", "dungeon_one_match", 3, true),
            Definition(90003, "b", "dungeon_one_match", 5, false)]);
        var counters = VariousBuffPolicy.Grant([], [], [], mixed, 0, 0, at, _ => throw new InvalidOperationException("unexpected draw"));
        That(counters.Saved is [{ BuffId: 90001, Remaining: 3 }] && counters.Transient is [{ BuffId: 90003, Remaining: 5 }],
            "同次来源可组合保存型和非保存型次数资格，不把剩余次数写成到期时间");
        var consumed = VariousBuffPolicy.Consume(counters.Saved, VariousBuffLifetime.DungeonEntries, new HashSet<int> { 90001 });
        That(consumed is [{ Remaining: 2 }] && counters.Saved[0].Remaining == 3, "副本次数只派生一份扣次结果，原快照保持只读");
        var effects = counters.Saved.Select(row => row with { Lifetime = VariousBuffLifetime.EffectApplications }).ToImmutableArray();
        That(VariousBuffPolicy.Consume(effects, VariousBuffLifetime.DungeonEntries, new HashSet<int> { 90001 })[0].Remaining == 3 &&
            VariousBuffPolicy.Consume(effects, VariousBuffLifetime.EffectApplications, new HashSet<int> { 90001 })[0].Remaining == 2,
            "效果应用次数由实际命中事件扣除，不冒充每次入场都扣");
        var old = new[] { new VariousBuffState(90001, 70001, VariousBuffLifetime.Timed, 1900000600000, 0),
            new VariousBuffState(90002, 70002, VariousBuffLifetime.Timed, 1900000600000, 0),
            new VariousBuffState(90003, 70003, VariousBuffLifetime.Timed, 1900000600000, 0) };
        That(VariousBuffPolicy.Covered(old, definitions[90001], definitions).SequenceEqual(new[] { 90001, 90002 }),
            "覆盖按元数据系统组处理，不清除不同组，也不依赖STK编号列表");
        var oneGroup = Binding(80005, [Definition(90001), Definition(90002)]);
        var last = VariousBuffPolicy.Grant([], [], [], oneGroup, 600000, 7, at, _ => 0);
        That(last.Granted.SequenceEqual(new[] { 90002 }) && last.Saved is [{ BuffId: 90002 }],
            "同批同组的最大一项按声明顺序覆盖，回执只报告最终实际保留资格");
        Console.WriteLine($"PASS {Count - before} various buff association, choice, coverage and lifetime assertions");
    }

    public static void RunResources(ItemResources resources)
    {
        var catalog = resources.RecoveryConsumables.VariousBuffs;
        var problems = catalog.Associations.Values.Where(row => row.Valid).SelectMany(row => row.ReachableBuffIds)
            .Distinct().Where(catalog.Definitions.ContainsKey).Select(id => catalog.Definitions[id]).Where(row => !row.BindingReady).ToArray();
        That(problems.Length == 0, "当前关联表元数据不存在未接入基础字段: " + string.Join(";", problems.Select(row =>
            row.BuffId + ":" + string.Join(",", row.BindingErrors))));
        That(catalog.Bind(10008219) is { Ready: true, Association.Random: true } random &&
            random.Association.Choices.Select(choice => choice.Weight).SequenceEqual(new[] { 60, 30, 10 }) &&
            resources.RecoveryConsumables.TryGet(10008219, out var potion) && potion.VariousBuff is not null,
            "当前随机史诗药剂通过资源关联自动接入，不再维护该STK专用分支");
        That(catalog.Definitions[237] is { BindingReady: true, BanGroup: 2, OverlapMax: 1 } &&
            catalog.Definitions[1151].ScopeRules.Length == 6 && catalog.Definitions[306].Effects.First().Parameters.Length == 2,
            "覆盖字段、多个副本范围与多参数效果完整保留，未实现name不损坏关联层");
        var aliases = new VariousBuffCatalog(catalog.Definitions, catalog.Items.Add(900000001, catalog.Items[490005358]));
        That(EpicDropBuffDefinition.Load(aliases)[900000001] is { BuffId: 1239, HellRewardOnly: true } &&
            aliases.Families[catalog.Items[490005358].SystemGroup].Contains(900000001),
            "同Buff新增STK别名自动继承明确范围和覆盖组，无须在服务端补该编号");
    }
}
