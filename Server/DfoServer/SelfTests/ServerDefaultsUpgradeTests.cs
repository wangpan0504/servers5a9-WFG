using DfoServer.Game.Events;
using DfoServer.Game.Raids;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ServerDefaultsUpgradeTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-default-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyResetAndPreserve(Path.Combine(root, "existing"));
            VerifyInterruptedRetry(Path.Combine(root, "interrupted"));
            VerifyEventRollback(Path.Combine(root, "rollback"));
            VerifySupportPersistence(Path.Combine(root, "support"));
            VerifyLogs(Path.Combine(root, "logs"));
            VerifyEntryNpc();
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 服务端发行默认一次重置与后续保留断言");
    }

    static string Completed(string directory) => Path.Combine(directory, ".95jp-upgrades", "server-defaults-1", "completed");

    static void VerifyResetAndPreserve(string directory)
    {
        Directory.CreateDirectory(directory);
        var store = new ServerEventRepository(Path.Combine(directory, "game.db"));
        var events = new ServerEventService(store);
        events.Override(64, false); events.Override(312, false);
        var originalRaids = new RaidEntryStore(directory);
        originalRaids.Replace("{}");
        string originalRaidJson = originalRaids.ReadJson();
        new SupportCompanionStore(directory).Write(new(3, "huge", "entry", "entry"));
        That(ServerDefaultsUpgrade.Apply(directory, true), "旧安装本次升级重置一次");
        var now = DateTimeOffset.UtcNow;
        var current = new ServerEventService(new ServerEventRepository(Path.Combine(directory, "game.db")));
        current.ConfigureBattlePass(now.AddDays(-1), now.AddDays(1), true);
        That(ServerEventService.Definitions.All(entry => current.Read(entry.Id) is { Active: true, Schedule.OverrideActive: true, Schedule.Revision: > 0 }),
            "已装配生产链的十三项白名单活动全部有真实持久开启配置");
        var raids = new RaidEntryStore(directory);
        That(RaidEntrySchedule.Names.All(raid => Enumerable.Range(0, 7).All(day =>
            raids.IsOpen(raid, new DateTimeOffset(2026, 11, 2, 0, 0, 0, RaidEntrySchedule.Zone).AddDays(day)) &&
            raids.IsOpen(raid, new DateTimeOffset(2026, 11, 2, 23, 59, 59, RaidEntrySchedule.Zone).AddDays(day)))),
            "四类已支持团本每周七天首尾时刻全部开放");
        That(new SupportCompanionStore(directory).Read() == SupportCompanionConfiguration.Default,
            "升级将既有支援兵一次恢复默认关闭");
        string backupRoot = Path.GetDirectoryName(Completed(directory))!;
        That(File.ReadAllText(Path.Combine(backupRoot, "gm-raid-windows.json.before")) == originalRaidJson &&
            File.ReadAllText(Path.Combine(backupRoot, "events.before.json")).Contains("false", StringComparison.OrdinalIgnoreCase),
            "升级前原配置保留在本安装数据目录内");
        var off = current.Override(64, false);
        var schedule = current.Schedule(312, now.AddDays(2), now.AddDays(3));
        current.Override(10005, false);
        raids.Replace("{}");
        var selected = new SupportCompanionConfiguration(6, "graduate", "huge", "entry");
        new SupportCompanionStore(directory).Write(selected);
        That(!ServerDefaultsUpgrade.Apply(directory, true), "同代次重启不再次执行升级");
        var restarted = new ServerEventService(new ServerEventRepository(Path.Combine(directory, "game.db")));
        That(restarted.Read(64) == off && restarted.Read(312) == schedule && !restarted.Read(10005).Active &&
            RaidEntrySchedule.Names.All(raid => !new RaidEntryStore(directory).IsOpen(raid, now)) &&
            new SupportCompanionStore(directory).Read() == selected,
            "活动关闭及时间表、星河关闭、团本空表和支援兵选择均跨重启保留");
        File.WriteAllText(Completed(directory), "corrupt");
        Reject<InvalidDataException>(() => ServerDefaultsUpgrade.Apply(directory, true), "坏代次标记不能被当作新装反复重置");
    }

    static void VerifyInterruptedRetry(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "gm-raid-windows.json.tmp"));
        Reject<UnauthorizedAccessException>(() => ServerDefaultsUpgrade.Apply(directory, true), "跨文件写入失败拒绝报告升级成功");
        That(!File.Exists(Completed(directory)), "失败不产生完成代次");
        var store = new ServerEventRepository(Path.Combine(directory, "game.db"));
        var committed = store.Read(64);
        Directory.Delete(Path.Combine(directory, "gm-raid-windows.json.tmp"));
        That(ServerDefaultsUpgrade.Apply(directory, true) && store.Read(64) == committed,
            "监听前中断重试补齐未完成文件，已提交同值活动不重复增加版本");
        That(File.Exists(Path.Combine(Path.GetDirectoryName(Completed(directory))!, "gm-raid-windows.json.before.absent")),
            "中断重试保留原始缺文件证据");
    }

    static void VerifyEventRollback(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "game.db");
        var store = new ServerEventRepository(path);
        using var db = new SqliteGameDatabase(path).Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER default_upgrade_fail BEFORE INSERT ON server_events WHEN NEW.event_id=312 BEGIN SELECT RAISE(ABORT,'injected'); END";
        command.ExecuteNonQuery();
        Reject<SqliteException>(() => ServerDefaultsUpgrade.Apply(directory, true), "活动批量晚期失败原子回滚");
        That(store.Read(64).Revision == 0 && store.Read(266).Revision == 0 && !File.Exists(Completed(directory)),
            "批量失败没有前半套活动或完成标记");
        Reject<InvalidDataException>(() => store.ResetDefaults([64, 65535]), "未登记占位活动不能加入发行默认白名单");
    }

    static void VerifySupportPersistence(string directory)
    {
        var store = new SupportCompanionStore(directory);
        var settings = new GameDebugSettings(store.Read(), store.Write);
        var chosen = new SupportCompanionConfiguration(3, "huge", "graduate", "entry");
        settings.SupportConfiguration = chosen;
        That(new GameDebugSettings(store.Read(), store.Write).SupportConfiguration == chosen,
            "名单和三个档次作为同一快照落盘并恢复");
        settings.SummonWangFugui = false;
        That(store.Read() == chosen with { SelectionMask = 2 }, "旧单开关接口也持久化且保留其它名单和档次");
        var failing = new GameDebugSettings(chosen, _ => throw new IOException("injected"));
        Reject<IOException>(() => failing.SupportCompanions = 0, "支援兵落盘失败向原命令报告失败");
        That(failing.SupportConfiguration == chosen, "落盘失败不能先发布内存成功状态");
        Parallel.For(0, 24, index => settings.SupportConfiguration = chosen with { SelectionMask = index % 2 == 0 ? 3 : 6 });
        That(store.Read() == settings.SupportConfiguration, "并发更新的最终磁盘与内存属于同一完整快照");
        Reject<ArgumentOutOfRangeException>(() => settings.SupportConfiguration = chosen with { SelectionMask = 5 },
            "多辅助非法组合仍被原合同拒绝");
        File.WriteAllText(Path.Combine(directory, "support-companions.json"), "{}");
        Reject<ArgumentOutOfRangeException>(() => store.Read(), "坏持久配置不能静默覆盖为默认");
    }

    static void VerifyLogs(string directory)
    {
        That(!ServerOptions.Load().FileLogsEnabled, "普通服务端文件日志默认关闭");
        That(ServerOptions.Load(DfoServer.Application.ServerCommand.Parse(["--file-logs", "On"]).Options).FileLogsEnabled &&
            !ServerOptions.Load(DfoServer.Application.ServerCommand.Parse(["--file-logs", "Off"]).Options).FileLogsEnabled,
            "文件日志显式开关严格装载");
        Reject<InvalidDataException>(() => ServerOptions.Load(DfoServer.Application.ServerCommand.Parse(["--file-logs", "true"]).Options),
            "文件日志非法开关拒绝");
        var entry = new DfoServer.Network.Management.GmAuditEntry(DateTimeOffset.Now, "test", "support", "-", "off", true, "", "", "OK");
        using (var log = new ServerLog(directory, fileEnabled: false)) { log.Write("server_ready", new { Pid = 1 }); log.Flush(); }
        new DfoServer.Network.Management.GmAudit(Path.Combine(directory, "gm-audit.jsonl"), false).Append(entry);
        That(!Directory.Exists(directory), "关闭日志不创建server或GM文件与目录");
        using (var log = new ServerLog(directory)) { log.Write("server_ready", new { Pid = 1 }); log.Flush(); }
        new DfoServer.Network.Management.GmAudit(Path.Combine(directory, "gm-audit.jsonl")).Append(entry);
        That(File.ReadAllText(Path.Combine(directory, "server.jsonl")).Contains("server_ready", StringComparison.Ordinal) &&
            new DfoServer.Network.Management.GmAudit(Path.Combine(directory, "gm-audit.jsonl")).Recent(1).Length == 1,
            "显式开启后服务端与GM文件日志可读回");
    }

    static void VerifyEntryNpc()
    {
        const string originalText = "[npc]\n2 `[left]` 443 125 0\n[/npc]\n[virtual movable area] 1 2 3 4";
        string restoredText = DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.RestoreText("map/common/(r)gate_seria.map", originalText, true, _ => true);
        That(restoredText.Contains("400001102 `[right]` 535 165 0", StringComparison.Ordinal) &&
            restoredText.Replace("\n400001102 `[right]` 535 165 0\n", "", StringComparison.Ordinal) == originalText,
            "递归import在精确原脚本追加权威NPC，其它原文逐字符保留");
        That(DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.RestoreText("map/common/(r)gate_seria.map", restoredText, true, _ => true) == restoredText,
            "原脚本及客户端world已有同NPC时不重复追加");
        Reject<InvalidDataException>(() => DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.RestoreText("map/common/(r)gate_seria.map", "[npc] broken [/npc]", true, _ => true),
            "坏原NPC段不按松散数字猜补");
        var source = new DfoServer.GameWorld.Towns.TownMap("map/common/(r)gate_seria.map", [], [], true,
            [new(2, 443, 125)]);
        var restored = DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(source, true,
            path => path == DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Resource);
        That(restored.Npcs.SequenceEqual(source.Npcs.Concat([new DfoServer.GameWorld.Towns.TownNpc(400001102, 535, 165)])) &&
            ReferenceEquals(restored.Bounds, source.Bounds) && ReferenceEquals(restored.Links, source.Links),
            "生产入口只追加权威原NPC身份位置，所有既有NPC与几何原样保留");
        That(ReferenceEquals(restored, DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(restored, true, _ => true)),
            "原生已有NPC优先且重复装配幂等");
        That(ReferenceEquals(source, DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(source, false, _ => false)),
            "关闭活动不追加也不要求NPC资源");
        var other = source with { Path = "map/common/other.map" };
        That(ReferenceEquals(other, DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(other, true, _ => false)),
            "其它地图不能借入口恢复扩大NPC注册");
        Reject<FileNotFoundException>(() => DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(source, true, _ => false),
            "缺少精确原NPC资源拒绝补写");
        Reject<InvalidDataException>(() => DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(source with { IsRoom = false }, true, _ => true),
            "同名非房间地图拒绝补写");
        Reject<InvalidDataException>(() => DfoServer.GameWorld.Events.EpicRoad2019EntryNpc.Restore(restored with { Npcs = [.. restored.Npcs, new(400001102, 535, 165)] }, true, _ => true),
            "重复原NPC不得继续追加或静默宣告唯一");
    }
}
