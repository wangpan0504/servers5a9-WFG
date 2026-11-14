using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Projection;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarBattlefieldTests
{
    sealed class FacilityDraw : Random
    {
        internal int Draws { get; private set; }
        public override int Next(int maxValue) => Draws++ % maxValue;
    }

    internal static Task RunFacilitiesAsync(PvfArchive archive)
    {
        int beforeCount = Count;
        string path = RaidCatalog.Load(archive).Find(2)!.Path;
        var script = FiendwarScript.Parse(path, archive.ReadTokens(path), archive.ReadTokens(path + ".symbol"), archive.ResolveString);
        var dungeons = DungeonCatalog.ParseDirectory(archive.ReadTokens("dungeon/dungeon.lst"), archive.ResolveString, "dungeon");
        var maps = DungeonCatalog.ParseDirectory(archive.ReadTokens("map/map.lst"), archive.ResolveString, "map");
        // 本组只验证DGN/MAP/MOB合同，不加载无关物品或数据库；计费由既有真实TCP验证。
        var checkedResources = RaidResourceLoader.Check(script.Script, ImmutableDictionary<int, string>.Empty,
            id => dungeons.GetValueOrDefault(id), id => maps.GetValueOrDefault(id), archive.Contains,
            archive.ReadTokens, archive.ResolveString, _ => true);
        var entries = RaidDungeonCatalog.Load(archive, checkedResources, id => maps.GetValueOrDefault(id), 2);
        var catalog = new FiendwarFacilityCatalog(script, entries, DungeonRoomCatalog.Load(archive));
        That(catalog.Areas.Count == 10 && entries.Ready, "真实正式目录预计算十处固定设施，无额外普通副本目录");
        foreach (int area in new[] { 19, 21, 23 })
            That(catalog.Areas[area].Mazes.Select(row => row.Kind).SequenceEqual(new[] { 7, 8, 9 }), "三前哨头像由Boss MOB确定");
        foreach (int area in new[] { 27, 28, 29 })
            That(catalog.Areas[area].Mazes.Select(row => row.Kind).SequenceEqual(new[] { 12, 11, 10 }), "兵营真实序为光暗重力，不按ordinal加10");
        That(catalog.Areas[15].Mazes is [{ Kind: 5 }] && catalog.Areas[17].Mazes is [{ Kind: 6 }] &&
            catalog.Areas[30].Mazes is [{ Kind: 13 }] && catalog.Areas[31].Mazes is [{ Kind: 13 }], "雷达炮台与双本体取各自真实Boss头像");
        var random = new FacilityDraw();
        var battle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, fiendwar: script,
            fiendwarFacilities: catalog, fiendwarMazeRandom: random);
        var engine = new FiendwarBattlefield(script, new());
        var initial = battle.View;
        That(initial.FiendwarFacilities.Count == 8 && random.Draws == 8, "首次展示前冻结八处已开放固定设施，本体尚未开放");
        var hidden = RaidCombatProjection.FiendwarUnitSnapshot(initial, true, false);
        That(initial.FiendwarFacilities.All(row => hidden[row.Key].Kind == 0 &&
            hidden[row.Key].Serial == row.Value.Target.EnemySerial), "初始设施头像按雷达遮罩，保留冻结serial");
        foreach (var row in initial.FiendwarFacilities)
        {
            int[] ordinals = catalog.Areas[row.Key].Mazes.Select(maze => maze.Ordinal).ToArray();
            var target = battle.FiendwarTarget(row.Value.Target.Dungeon, ordinals);
            That(target == row.Value.Target && battle.FiendwarTarget(row.Value.Target.Dungeon, ordinals) == target &&
                battle.IsFiendwarTargetCurrent(target, target!.Dungeon, target.MazeOrdinal), "同代选图和重进复用唯一固定遭遇");
            That(!battle.IsFiendwarTargetCurrent(target! with { Instance = Guid.NewGuid() }, target.Dungeon, target.MazeOrdinal) &&
                !battle.IsFiendwarTargetCurrent(target with { FacilitySerial = target.FacilitySerial + 1 }, target.Dungeon, target.MazeOrdinal) &&
                !battle.IsFiendwarTargetCurrent(target, target.Dungeon, target.MazeOrdinal + 1), "实例serial与maze任一不符均拒绝");
        }
        That(random.Draws == 8, "重复选择及身份校验不重新抽取迷宫");
        var now = initial.Fiendwar!.Started;
        var radarTarget = initial.FiendwarFacilities[15].Target;
        Install(engine.CompleteArea(battle.View.Fiendwar!, 15, now).State);
        That(!battle.View.FiendwarFacilities.ContainsKey(15) && !battle.AllowsFiendwarEntry(radarTarget.Dungeon) &&
            !battle.IsFiendwarTargetCurrent(radarTarget, radarTarget.Dungeon, radarTarget.MazeOrdinal), "雷达完成删除唯一遭遇并拒绝旧target");
        var shown = RaidCombatProjection.FiendwarUnitSnapshot(battle.View, true, false);
        That(battle.View.FiendwarFacilities.All(row => shown[row.Key].Kind == row.Value.Kind), "雷达完成在同次快照显露固定设施真实头像");
        Install(engine.CompleteArea(battle.View.Fiendwar!, 17, now).State);
        var batteryField = battle.View.Fiendwar!;
        var batteryTimer = batteryField.Timers.Single(row => row.Key.Kind == "activate_battery");
        That(batteryTimer.Value.End - batteryTimer.Value.Started == TimeSpan.FromSeconds(60), "炮台增益期限不乘设施恢复倍率");
        Install(engine.Tick(batteryField with { Timers = ImmutableDictionary<FiendwarTimerKey, FiendwarTimer>.Empty.Add(batteryTimer.Key, batteryTimer.Value) }, batteryTimer.Value.End).State);
        That(battle.View.Symbols["[battery state]"] == 0 && !battle.View.FiendwarFacilities.ContainsKey(17) &&
            !battle.AllowsFiendwarEntry(catalog.Areas[17].Dungeon), "炮台到期只关增益，不重开DGN或重生固定头像");
        foreach (int area in new[] { 19, 21, 23, 27, 28, 29 })
        {
            var previous = battle.View.FiendwarFacilities[area];
            var resource = catalog.Areas[area];
            int draws = random.Draws;
            Install(engine.CompleteArea(battle.View.Fiendwar!, area, now).State);
            That(!battle.View.FiendwarFacilities.ContainsKey(area) && !battle.AllowsFiendwarEntry(resource.Dungeon), "设施通关移除头像并关闭准入");
            if (area < 27)
                Install(engine.SetSymbol(battle.View.Fiendwar!, resource.RestoreSymbol!, 1, now).State);
            else
            {
                var field = battle.View.Fiendwar!;
                var timer = field.Timers.Single(row => row.Key.Kind == (area == 27 ? "top_restore_center" : area == 28 ? "mid_restore_center" : "bot_restore_center"));
                That(timer.Value.End - timer.Value.Started == TimeSpan.FromSeconds(1440), "真实兵营恢复timer沿统一倍率为1440秒");
                // 只隔离其它已测兵线的到期，保留真实恢复规则及其生成军团动作。
                Install(engine.Tick(field with { Timers = ImmutableDictionary<FiendwarTimerKey, FiendwarTimer>.Empty.Add(timer.Key, timer.Value) }, timer.Value.End).State);
            }
            var restored = battle.View.FiendwarFacilities[area];
            That(restored.Target.Generation == previous.Target.Generation + 1 && restored.Target.FacilitySerial > previous.Target.FacilitySerial &&
                random.Draws == draws + 1 && battle.View.Fiendwar!.Dungeons[resource.Dungeon] == "open" && battle.AllowsFiendwarEntry(resource.Dungeon),
                "已证恢复在同次提交重开DGN增代分配serial并均匀重抽一次");
            That(!battle.IsFiendwarTargetCurrent(previous.Target, resource.Dungeon, previous.Target.MazeOrdinal) &&
                battle.IsFiendwarTargetCurrent(restored.Target, resource.Dungeon, restored.Target.MazeOrdinal), "旧代target不能冒充新恢复遭遇");
            using var stale = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Fiendwar)
                { FiendwarTarget = previous.Target }, checked((uint)resource.Dungeon), [], mazeOrdinal: previous.Target.MazeOrdinal, coinLimit: 1);
            That(stale is null, "Run预留拒绝旧选择，不能自动套用当前generation");
        }
        That(!battle.View.FiendwarFacilities.ContainsKey(15), "其它设施多次恢复不复活雷达");
        var capped = battle.View with { FiendwarFacilities = battle.View.FiendwarFacilities.Remove(19) };
        bool overflow = false;
        try
        {
            typeof(AntonRaidBattle).GetMethod("ProjectFiendwar", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(battle, [capped, capped.Fiendwar! with { NextUnit = uint.MaxValue }]);
        }
        catch (TargetInvocationException error) when (error.InnerException is OverflowException) { overflow = true; }
        That(overflow && battle.View.Fiendwar!.NextUnit < uint.MaxValue,
            "设施serial超出原生u32时在候选提交前拒绝，不把溢出推迟到发送泵");
        Console.WriteLine($"PASS {Count - beforeCount} 漩涡真实固定设施与generation断言");
        return Task.CompletedTask;

        void Install(FiendwarFieldState field)
        {
            // 只装配由真实Battlefield产生的候选；不为测试扩大生产变更入口。
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var view = (AntonRaidBattleView)typeof(AntonRaidBattle).GetMethod("ProjectFiendwar", flags)!
                .Invoke(battle, [battle.View, field])!;
            typeof(AntonRaidBattle).GetField("current", flags)!.SetValue(battle, view);
        }
    }
}
