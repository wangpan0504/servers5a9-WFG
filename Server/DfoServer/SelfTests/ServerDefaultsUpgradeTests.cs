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
        var selected = new SupportCompanionConfiguration(3, "graduate", "huge", "entry");
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
        Parallel.For(0, 24, index => settings.SupportConfiguration = chosen with { SelectionMask = index % 2 == 0 ? 3 : 2 });
        That(store.Read() == settings.SupportConfiguration, "并发更新的最终磁盘与内存属于同一完整快照");
        Reject<ArgumentOutOfRangeException>(() => settings.SupportConfiguration = chosen with { SelectionMask = 5 },
            "退役辅助和多辅助非法组合均拒绝");
        if (OperatingSystem.IsWindows()) VerifySupportFileReplacement(directory, store, settings);
        string path = Path.Combine(directory, "support-companions.json");
        foreach (int oldMask in new[] { 1, 3, 4, 6, 8, 10 })
        {
            var old = chosen with { SelectionMask = oldMask };
            if (oldMask > 3)
                Reject<ArgumentOutOfRangeException>(() => store.Write(old), "新的配置写入不能重新启用退役职业");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(old));
            var normalized = old with { SelectionMask = oldMask & 3 };
            That(store.Read() == normalized && System.Text.Json.JsonSerializer.Deserialize<SupportCompanionConfiguration>(
                File.ReadAllText(path)) == normalized, "旧合法选择移除退役位并持久化，允许光兵与三个档次完整保留");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, stamp);
            That(new GameDebugSettings(store.Read(), store.Write).SupportConfiguration == normalized && File.GetLastWriteTimeUtc(path) == stamp,
                "同配置重启不再重写或反复清零");
        }
        foreach (int invalid in new[] { -1, 5, 12, 16 })
        {
            string raw = System.Text.Json.JsonSerializer.Serialize(chosen with { SelectionMask = invalid });
            File.WriteAllText(path, raw);
            Reject<ArgumentOutOfRangeException>(() => store.Read(), "坏旧mask不能借归一迁移洗成允许状态");
            That(File.ReadAllText(path) == raw, "坏配置读取失败保留原文件");
        }
        File.WriteAllText(Path.Combine(directory, "support-companions.json"), "{}");
        Reject<ArgumentOutOfRangeException>(() => store.Read(), "坏持久配置不能静默覆盖为默认");
    }

    static void VerifySupportFileReplacement(string directory, SupportCompanionStore store, GameDebugSettings settings)
    {
        string path = Path.Combine(directory, "support-companions.json");
        var before = settings.SupportConfiguration with { SelectionMask = 2 };
        settings.SupportConfiguration = before;
        var expected = before;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            // 连续覆盖期间不插入读操作，旧句柄不应阻止新快照发布。
            for (int index = 0; index < 24; index++)
            {
                expected = before with { SelectionMask = index % 2 == 0 ? 3 : 1 };
                settings.SupportConfiguration = expected;
            }
            That(store.Read() == expected && settings.SupportConfiguration == expected &&
                new GameDebugSettings(store.Read(), store.Write).SupportConfiguration == expected,
                "共享删除读句柄存活时连续24次真实保存保留最终完整快照且可重开");
            using var reader = new StreamReader(held, leaveOpen: true);
            That(System.Text.Json.JsonSerializer.Deserialize<SupportCompanionConfiguration>(reader.ReadToEnd()) == before && before != expected,
                "替换后旧读句柄仍只读到原配置，不混入新名单和档次");
        }
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject<IOException>(() => settings.SupportConfiguration = before, "不共享删除的真实读句柄拒绝替换");
            That(store.Read() == expected && settings.SupportConfiguration == expected &&
                Directory.GetFiles(directory, "support-companions.json.*.tmp").Length == 0,
                "共享冲突失败保持磁盘内存快照且回收本次临时文件");
        }
        settings.SupportConfiguration = before;
        That(store.Read() == before && settings.SupportConfiguration == before, "解除真实共享冲突后仍可保存");
        var attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            Reject<UnauthorizedAccessException>(() => settings.SupportConfiguration = expected, "只读配置文件拒绝替换");
            That(store.Read() == before && settings.SupportConfiguration == before &&
                Directory.GetFiles(directory, "support-companions.json.*.tmp").Length == 0,
                "只读拒绝不发布内存、不覆盖磁盘且回收临时文件");
        }
        finally { File.SetAttributes(path, attributes); }
        settings.SupportConfiguration = expected;
        That(store.Read() == expected && settings.SupportConfiguration == expected, "解除只读属性后仍可保存");

        // 旧合法名单的读取需要回写归一结果，失败不能返回尚未持久化的配置。
        var legacy = expected with { SelectionMask = 6 };
        var normalized = legacy with { SelectionMask = 2 };
        string raw = System.Text.Json.JsonSerializer.Serialize(legacy);
        File.WriteAllText(path, raw);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject<IOException>(() => store.Read(), "旧名单迁移遇到不共享删除读句柄时报告真实写入失败");
            That(File.ReadAllText(path) == raw && Directory.GetFiles(directory, "support-companions.json.*.tmp").Length == 0,
                "迁移共享冲突保留旧JSON和全部档次且清理临时文件");
        }
        That(store.Read() == normalized && System.Text.Json.JsonSerializer.Deserialize<SupportCompanionConfiguration>(
            File.ReadAllText(path)) == normalized, "解除共享冲突后读取真实迁移旧名单并完整保留档次");
        File.WriteAllText(path, raw);
        attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            Reject<UnauthorizedAccessException>(() => store.Read(), "旧名单迁移遇到只读目标时报告真实写入失败");
            That(File.ReadAllText(path) == raw && Directory.GetFiles(directory, "support-companions.json.*.tmp").Length == 0,
                "迁移只读拒绝保留旧JSON和全部档次且清理临时文件");
        }
        finally { File.SetAttributes(path, attributes); }
        That(store.Read() == normalized && System.Text.Json.JsonSerializer.Deserialize<SupportCompanionConfiguration>(
            File.ReadAllText(path)) == normalized, "解除只读属性后读取真实迁移旧名单并完整保留档次");
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
