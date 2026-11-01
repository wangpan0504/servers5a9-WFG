using DfoServer.Persistence;

namespace DfoServer;

// 自测进程开启 SQLite 连接池后，池里空闲连接仍持有 game.db/-wal/-shm 句柄，Windows 上删除目录会失败，
// 直接复制或读取主库文件也可能拿到未检查点的旧内容。测试删改临时库文件统一经此入口，先释放池再操作；
// 未开池时（离线工具）与直接调用 System.IO 完全一致。
internal static class TestFiles
{
    public static void DeleteDirectory(string path, bool recursive = false)
    {
        SqliteDatabaseBootstrap.ReleaseRuntimePool();
        Directory.Delete(path, recursive);
    }

    public static void DeleteFile(string path)
    {
        SqliteDatabaseBootstrap.ReleaseRuntimePool();
        File.Delete(path);
    }

    public static void Copy(string source, string target, bool overwrite = false)
    {
        SqliteDatabaseBootstrap.ReleaseRuntimePool();
        File.Copy(source, target, overwrite);
    }

    public static byte[] ReadAllBytes(string path)
    {
        SqliteDatabaseBootstrap.ReleaseRuntimePool();
        return File.ReadAllBytes(path);
    }
}