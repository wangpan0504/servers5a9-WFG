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
        if (OperatingSystem.IsWindows())
        {
            VerifySupportFileReplacement(directory, store, settings);
            VerifySupportReplacementRecovery(Path.Combine(directory, "replacement-faults"));
        }
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

    static void VerifySupportReplacementRecovery(string directory)
    {
        var before = new SupportCompanionConfiguration(2, "huge", "graduate", "entry");
        var after = before with { SelectionMask = 1 };
        string beforeJson = System.Text.Json.JsonSerializer.Serialize(before);
        string afterJson = System.Text.Json.JsonSerializer.Serialize(after);
        const int unableToRemove = unchecked((int)0x80070497);

        string freshDirectory = Path.Combine(directory, "fresh");
        That(new SupportCompanionStore(freshDirectory).Read() == SupportCompanionConfiguration.Default && !Directory.Exists(freshDirectory),
            "干净首次读取仍返回默认配置且不创建目录");
        Directory.CreateDirectory(freshDirectory);
        That(new SupportCompanionStore(freshDirectory).Read() == SupportCompanionConfiguration.Default,
            "已有空目录且无目标或遗留源仍属于首次配置");

        string recoveringDirectory = Path.Combine(directory, "recover");
        new SupportCompanionStore(recoveringDirectory).Write(before);
        int attempts = 0;
        string? sourcePath = null;
        bool sameSource = true, unpublished = true, oldTarget = true;
        GameDebugSettings? recovering = null;
        var recoveringStore = new SupportCompanionStore(recoveringDirectory, (source, target) =>
        {
            sourcePath ??= source;
            sameSource &= source == sourcePath && File.ReadAllText(source) == afterJson;
            oldTarget &= File.ReadAllText(target) == beforeJson;
            unpublished &= recovering!.SupportConfiguration == before;
            if (++attempts < 4) throw new IOException("injected replace 1175", unableToRemove);
            // 这里只验证重试与发布顺序；真实Replace由原24次连续保存用例覆盖。
            File.Copy(source, target, true);
            File.Delete(source);
        });
        recovering = new GameDebugSettings(recoveringStore.Read(), recoveringStore.Write);
        recovering.SupportConfiguration = after;
        That(attempts == 4 && sameSource && oldTarget && unpublished && recoveringStore.Read() == after &&
            recovering.SupportConfiguration == after && !File.Exists(sourcePath),
            "1175限定恢复沿用同一完整源，第四次确定性安装成功后才发布内存");

        foreach (bool blockCleanup in new[] { false, true })
        {
            string exhaustedDirectory = Path.Combine(directory, blockCleanup ? "cleanup-blocked" : "exhausted");
            new SupportCompanionStore(exhaustedDirectory).Write(before);
            string path = Path.Combine(exhaustedDirectory, "support-companions.json");
            var fault = new IOException("injected exhausted 1175", unableToRemove);
            IOException? observed = null;
            FileStream? held = null;
            bool rejectReplacement = true;
            attempts = 0;
            sourcePath = null;
            sameSource = true;
            var exhaustedStore = new SupportCompanionStore(exhaustedDirectory, (source, target) =>
            {
                if (!rejectReplacement) { File.Replace(source, target, null); return; }
                sourcePath ??= source;
                sameSource &= source == sourcePath && target == path && File.ReadAllText(source) == afterJson;
                if (blockCleanup) held ??= new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                attempts++;
                throw fault;
            });
            var exhausted = new GameDebugSettings(exhaustedStore.Read(), exhaustedStore.Write);
            try
            {
                try { exhausted.SupportConfiguration = after; }
                catch (IOException error) { observed = error; }
                That(ReferenceEquals(observed, fault) && attempts == 4 && sameSource &&
                    exhausted.SupportConfiguration == before && File.ReadAllText(path) == beforeJson,
                    "1175耗尽四次后原异常向上传递，磁盘与内存仍是已提交快照");
                That(blockCleanup ? File.Exists(sourcePath) && fault.Data["SupportCompanionCleanupError"] is IOException
                    : !File.Exists(sourcePath) && !fault.Data.Contains("SupportCompanionCleanupError"),
                    "临时源可清理则回收，真实清理冲突保留源且不能遮蔽原保存异常");
            }
            finally { held?.Dispose(); if (File.Exists(sourcePath)) File.Delete(sourcePath); }
            rejectReplacement = false;
            exhausted.SupportConfiguration = after;
            That(exhaustedStore.Read() == after && exhausted.SupportConfiguration == after && attempts == 4 &&
                Directory.GetFiles(exhaustedDirectory, "*.tmp").Length == 0,
                "1175耗尽或清理占用解除后，同一设置实例仍能真实保存，不重播旧异常");
        }

        foreach (int code in new[] { unchecked((int)0x80070020), unchecked((int)0x80130497) })
        {
            string rejectedDirectory = Path.Combine(directory, $"rejected-{unchecked((uint)code):X8}");
            new SupportCompanionStore(rejectedDirectory).Write(before);
            var fault = new IOException("injected non-retryable replacement", code);
            IOException? observed = null;
            attempts = 0;
            var rejectedStore = new SupportCompanionStore(rejectedDirectory, (_, _) => { attempts++; throw fault; });
            var rejected = new GameDebugSettings(rejectedStore.Read(), rejectedStore.Write);
            try { rejected.SupportConfiguration = after; }
            catch (IOException error) { observed = error; }
            That(ReferenceEquals(observed, fault) && attempts == 1 && rejectedStore.Read() == before &&
                rejected.SupportConfiguration == before && Directory.GetFiles(rejectedDirectory, "*.tmp").Length == 0,
                "共享冲突码及仅低位相同的非Win32错误均不重试、不发布内存");
        }

        foreach (int code in new[] { unchecked((int)0x80070498), unchecked((int)0x80070499) })
        {
            string partialDirectory = Path.Combine(directory, $"partial-{unchecked((uint)code):X8}");
            new SupportCompanionStore(partialDirectory).Write(before);
            string path = Path.Combine(partialDirectory, "support-companions.json");
            var fault = new IOException("injected partial replacement", code);
            IOException? observed = null;
            attempts = 0;
            sourcePath = null;
            var partialStore = new SupportCompanionStore(partialDirectory, (source, target) =>
            {
                attempts++;
                sourcePath = source;
                // 1176允许旧目标已丢失；1177只注入错误码，不假造其完整原生失败过程。
                if (code == unchecked((int)0x80070498)) File.Delete(target);
                throw fault;
            });
            var partial = new GameDebugSettings(partialStore.Read(), partialStore.Write);
            try { partial.SupportConfiguration = after; }
            catch (IOException error) { observed = error; }
            That(observed is not null && ReferenceEquals(observed.InnerException, fault) && observed.HResult == code &&
                observed.Message.Contains("partial replacement", StringComparison.Ordinal) && observed.Message.Contains(sourcePath!, StringComparison.Ordinal) &&
                attempts == 1 && partial.SupportConfiguration == before &&
                Equals(observed.Data["SupportCompanionRecoveryPath"], sourcePath) &&
                Equals(fault.Data["SupportCompanionRecoveryPath"], sourcePath) && File.ReadAllText(sourcePath!) == afterJson &&
                (code == unchecked((int)0x80070498) ? !File.Exists(path) : File.ReadAllText(path) == beforeJson),
                "1176与1177不重试、不发布内存，可见错误保留原始异常和HResult及完整恢复源，不擅自回滚目标");
            // 该命令只使用debugSettings，明确不装配仓储或owner，不启动数据库或服务。
            var gmSettings = new GameDebugSettings(before, _ => throw observed!);
            string answer = DfoServer.Development.DevelopmentGm.Answer("support-companions off", "support-test",
                null!, null!, _ => null, debugSettings: gmSettings);
            That(answer.StartsWith("ERR ", StringComparison.Ordinal) && answer.Contains("partial replacement", StringComparison.Ordinal) &&
                answer.Contains($"0x{unchecked((uint)code):X8}", StringComparison.Ordinal) && answer.Contains(sourcePath!, StringComparison.Ordinal) &&
                gmSettings.SupportConfiguration == before,
                "实际GM回执显示部分完成标识、HResult和恢复路径，错误仍不发布设置");
            if (code == unchecked((int)0x80070498))
            {
                IOException? reopened = null;
                try { _ = new SupportCompanionStore(partialDirectory).Read(); }
                catch (IOException error) { reopened = error; }
                That(reopened is not null && reopened.Message.Contains(sourcePath!, StringComparison.Ordinal) &&
                    Equals(reopened.Data["SupportCompanionRecoveryPath"], sourcePath) && !File.Exists(path) && File.ReadAllText(sourcePath!) == afterJson,
                    "新Store重开遇到缺目标及遗留源明确拒绝默认配置，保留源及缺失目标现场");
            }
        }

        var legacy = before with { SelectionMask = 6 };
        string legacyJson = System.Text.Json.JsonSerializer.Serialize(legacy);
        foreach (bool recover in new[] { true, false })
        {
            string retryDirectory = Path.Combine(directory, recover ? "migration-recover" : "migration-exhausted");
            Directory.CreateDirectory(retryDirectory);
            string path = Path.Combine(retryDirectory, "support-companions.json");
            File.WriteAllText(path, legacyJson);
            var fault = new IOException("injected migration 1175", unableToRemove);
            IOException? observed = null;
            SupportCompanionConfiguration? returned = null;
            attempts = 0;
            sourcePath = null;
            sameSource = true;
            oldTarget = true;
            var retryStore = new SupportCompanionStore(retryDirectory, (source, target) =>
            {
                sourcePath ??= source;
                sameSource &= source == sourcePath && File.ReadAllText(source) == beforeJson;
                oldTarget &= File.ReadAllText(target) == legacyJson;
                if (++attempts < 4 || !recover) throw fault;
                // 确定性注入成功不叠加最后一次真实Replace的外部竞争。
                File.Copy(source, target, true);
                File.Delete(source);
            });
            try { returned = retryStore.Read(); }
            catch (IOException error) { observed = error; }
            That(attempts == 4 && sameSource && oldTarget && Directory.GetFiles(retryDirectory, "*.tmp").Length == 0 &&
                (recover ? observed is null && returned == before && File.ReadAllText(path) == beforeJson
                    : ReferenceEquals(observed, fault) && returned is null && File.ReadAllText(path) == legacyJson),
                "旧名单迁移1175恢复后才返回完整归一结果，耗尽仍抛原错误并保持旧文件");
        }

        string migrationDirectory = Path.Combine(directory, "migration-partial");
        Directory.CreateDirectory(migrationDirectory);
        File.WriteAllText(Path.Combine(migrationDirectory, "support-companions.json"), legacyJson);
        var migrationFault = new IOException("injected migration 1176", unchecked((int)0x80070498));
        IOException? migrationError = null;
        attempts = 0;
        sourcePath = null;
        var migrationStore = new SupportCompanionStore(migrationDirectory, (source, target) =>
        { attempts++; sourcePath = source; File.Delete(target); throw migrationFault; });
        try { _ = migrationStore.Read(); }
        catch (IOException error) { migrationError = error; }
        That(migrationError is not null && ReferenceEquals(migrationError.InnerException, migrationFault) &&
            migrationError.HResult == migrationFault.HResult && migrationError.Message.Contains(sourcePath!, StringComparison.Ordinal) && attempts == 1 &&
            Equals(migrationError.Data["SupportCompanionRecoveryPath"], sourcePath) &&
            Equals(migrationFault.Data["SupportCompanionRecoveryPath"], sourcePath) && File.ReadAllText(sourcePath!) == beforeJson,
            "旧名单迁移部分失败可见错误保留原异常及HResult，不返回未提交配置且保留完整归一恢复源");
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
