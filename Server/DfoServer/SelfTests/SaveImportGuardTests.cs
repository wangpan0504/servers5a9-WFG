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
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 存档导入中断访问保护断言");
    }
}
