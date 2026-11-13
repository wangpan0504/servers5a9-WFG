using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SaveImportGuardTests
{
    internal static void RunCore()
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "S5A9-save-import-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var gate = new FileStream(Path.Combine(directory, "server.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            string database = Path.Combine(directory, "game.db");
            string pending = Path.Combine(directory, ".save-import.pending.json");
            SaveImportGuard.RequireNoPending(directory);
            That(!File.Exists(database), "没有导入记录时允许首次启动，检查本身不创建空库");

            File.WriteAllText(pending, "{\"PreviousDirectory\":\"recovery\"}");
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(directory), "未完成导入拒绝在缺主库时继续建库");
            That(!File.Exists(database) && File.ReadAllText(pending).Contains("recovery", StringComparison.Ordinal), "拒绝不创建空库、不删除恢复记录");

            File.WriteAllBytes(database, [1, 2, 3]);
            File.WriteAllBytes(database + "-wal", [4, 5, 6]);
            File.WriteAllText(pending, "corrupt");
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(directory), "损坏记录仍拒绝访问已有库");
            Bytes(File.ReadAllBytes(database), [1, 2, 3], "拒绝保持旧主库原样");
            Bytes(File.ReadAllBytes(database + "-wal"), [4, 5, 6], "拒绝保持旧WAL原样");

            File.Delete(pending);
            Directory.CreateDirectory(pending);
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(directory), "恢复记录路径被目录占用时也拒绝");
            Directory.Delete(pending);
            SaveImportGuard.RequireNoPending(directory);
            That(File.Exists(database + "-wal"), "完成外部恢复后检查可放行，仍不擅自处理侧车");

            string installation = Path.Combine(directory, "一体安装 空格");
            string integratedData = Path.Combine(installation, "ServerS5A9", "Data");
            Directory.CreateDirectory(integratedData);
            using var integratedGate = new FileStream(Path.Combine(integratedData, "server.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            string updateDirectory = Path.Combine(installation, ".95jp-update");
            string updateMarker = Path.Combine(updateDirectory, "active.json");
            string integratedDatabase = Path.Combine(integratedData, "game.db");
            SaveImportGuard.RequireNoPending(integratedData);
            Directory.CreateDirectory(updateDirectory);
            File.WriteAllText(updateMarker, "{\"schema\":1,\"id\":\"unfinished\"}");
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(integratedData), "更新中断后即使实例锁已重新取得仍拒绝创建数据库");
            That(!File.Exists(integratedDatabase) && File.Exists(updateMarker), "拒绝不建库也不清除更新恢复入口");
            File.WriteAllBytes(integratedDatabase, [7, 8, 9]);
            File.WriteAllBytes(integratedDatabase + "-wal", [10, 11]);
            File.WriteAllText(updateMarker, "corrupt");
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(integratedData), "损坏更新记录仍阻止直接服务及离线写库共用入口");
            Bytes(File.ReadAllBytes(integratedDatabase), [7, 8, 9], "更新未恢复时保留主库");
            Bytes(File.ReadAllBytes(integratedDatabase + "-wal"), [10, 11], "更新未恢复时保留侧车");
            File.Delete(updateMarker);
            Directory.CreateDirectory(updateMarker);
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(integratedData), "更新记录被目录占用时也拒绝");
            Directory.Delete(updateMarker);
            File.WriteAllText(updateMarker + ".previous", "corrupt");
            Reject<InvalidDataException>(() => SaveImportGuard.RequireNoPending(integratedData), "只有更新前代记录仍须恢复，不因主标记缺失放行");
            File.Delete(updateMarker + ".previous");
            SaveImportGuard.RequireNoPending(integratedData);
            That(File.Exists(integratedDatabase + "-wal"), "外部恢复收口后放行且不改动侧车");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 存档导入中断访问保护断言");
    }
}
