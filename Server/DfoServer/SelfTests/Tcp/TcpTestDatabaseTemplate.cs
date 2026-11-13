using System.Collections.Concurrent;
using DfoServer.Persistence;

namespace DfoServer.Testing;

// 每个轮次目录只按正式基线建一次空库，共享资源业务夹具初始化时按需复制该文件，
// 省去每组重复执行完整建表与结构校验；服务端仍对复制库执行常规迁移校验。
// 已有数据库（用例预置旧版本或重启阶段）不覆盖；无共享资源的独立进程冷启动仍由服务自行建库。
// 共享资源业务夹具可在构造时先复用模板，再完成真实默认初始化。
internal static class TcpTestDatabaseTemplate
{
    static readonly ConcurrentDictionary<string, Lazy<string>> templates = new(StringComparer.OrdinalIgnoreCase);

    public static void CopyIfMissing(string database, string runRoot)
    {
        if (File.Exists(database) || Environment.GetEnvironmentVariable("DFO_TCP_DB_TEMPLATE") == "0")
            return;
        string template = templates.GetOrAdd(Path.GetFullPath(runRoot), root => new Lazy<string>(() =>
        {
            string path = Path.Combine(root, ".db-template", "game.db");
            SqliteDatabaseBootstrap.Initialize(path);
            return path;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        TestFiles.Copy(template, database);
    }
}
