using static DfoServer.Testing.TestCheck;
using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // 仅本次 Run 的私有空库准备，不接受外部库、不跨轮共享、不缓存迁移结果。
    // 连接全部关闭（含释放自测连接池）后才能复制；无 writer 来自私有所有权，
    // 不是 FileShare 独占或“未看到 sidecar”就证明任意外部 writer 已退出。
    sealed class ReleasedDatabaseTemplate
    {
        readonly string source;

        public ReleasedDatabaseTemplate(string root)
        {
            string directory = Path.Combine(root, "released-template-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            source = Path.Combine(directory, "empty.db");
            _ = new CharacterRepository(source);
            SqliteDatabaseBootstrap.ReleaseRuntimePool();
            ValidateClosedEmpty(source);
        }

        public void CopyTo(string target)
        {
            RequireNoSidecars(source);
            RequireNoSidecars(target);
            // 不覆盖既有文件、不以硬链接共享可写数据库；所有对象随 Run 的 finally 清理。
            TestFiles.Copy(source, target, overwrite: false);
        }

        public static void RequireNoSidecars(string path)
        {
            foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix) || Directory.Exists(path + suffix))
                    throw new InvalidDataException("迁移空库准备包含未闭合的 SQLite sidecar");
        }

        public static void ValidateClosedEmpty(string path)
        {
            if (!File.Exists(path)) throw new InvalidDataException("迁移空库准备文件缺失");
            RequireNoSidecars(path);
            using (var db = Open(path))
            {
                if (!Equals(Query(db, "PRAGMA user_version"), (long)SqliteMigrations.CurrentVersion) ||
                    Query(db, "SELECT value FROM schema_metadata WHERE key='baseline_id'") is not string baseline ||
                    baseline != SqliteMigrations.BaselineId || Query(db, "SELECT count(*) FROM schema_metadata") is not 1L ||
                    Query(db, "PRAGMA integrity_check") is not "ok" ||
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is not 0L ||
                    Query(db, "PRAGMA journal_mode") is not "wal")
                    throw new InvalidDataException("迁移空库准备的基线、版本、WAL 或完整性不符");
                foreach (string table in TableNames(db).Where(table => table != "schema_metadata"))
                    if (Query(db, "SELECT count(*) FROM \"" + table.Replace("\"", "\"\"") + "\"") is not 0L)
                        throw new InvalidDataException("迁移空库准备不能携带业务或序号种子行");
            }
            RequireNoSidecars(path);
        }
    }

    static void VerifyReleasedTemplateContract(ReleasedDatabaseTemplate template, string root)
    {
        string directory = Path.Combine(root, "released-template-contract");
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "first.db"), second = Path.Combine(directory, "second.db");
        template.CopyTo(first);
        template.CopyTo(second);
        Check(Snapshot(first) == Snapshot(second), "关闭空库复制保持全部结构、rowid和元数据");
        ReleasedDatabaseTemplate.ValidateClosedEmpty(first);
        ReleasedDatabaseTemplate.ValidateClosedEmpty(second);
        string fresh = Path.Combine(directory, "fresh.db");
        _ = new CharacterRepository(fresh);
        Check(Snapshot(fresh) == Snapshot(second), "空库副本与正式从零建库的结构、rowid和种子状态等价");
        using (var db = Open(first)) Execute(db, "INSERT INTO accounts VALUES('template-contract',X'01',X'02',1,0,7)");
        Check(QueryOn(second, "SELECT count(*) FROM accounts") is 0L, "迁移准备副本写入互不污染");
        Reject<InvalidDataException>(() => ReleasedDatabaseTemplate.ValidateClosedEmpty(first), "拒绝带业务种子的准备对象");
        string changed = Snapshot(first);
        Reject<IOException>(() => template.CopyTo(first), "迁移准备不覆盖已有目标");
        Check(Snapshot(first) == changed, "拒绝覆盖后保留目标原有数据");
        using (var db = Open(second)) Execute(db, "PRAGMA user_version=0");
        Reject<InvalidDataException>(() => ReleasedDatabaseTemplate.ValidateClosedEmpty(second), "拒绝错误版本的准备对象");
        // 源负例先创建关闭的主文件，避免被文件缺失检查短路；目标门禁另用不存在的文件。
        string sidecarSource = Path.Combine(directory, "sidecar-source.db");
        template.CopyTo(sidecarSource);
        string sidecarTarget = Path.Combine(directory, "sidecar.db");
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            File.WriteAllText(sidecarSource + suffix, "incomplete");
            Reject<InvalidDataException>(() => ReleasedDatabaseTemplate.ValidateClosedEmpty(sidecarSource), "迁移准备拒绝检查带 sidecar 的源");
            TestFiles.DeleteFile(sidecarSource + suffix);
            File.WriteAllText(sidecarTarget + suffix, "incomplete");
            Reject<InvalidDataException>(() => template.CopyTo(sidecarTarget), "迁移准备拒绝目标残留 sidecar");
            Check(!File.Exists(sidecarTarget), "拒绝 sidecar 时不创建目标数据库");
            TestFiles.DeleteFile(sidecarTarget + suffix);
        }
        string third = Path.Combine(directory, "third.db");
        template.CopyTo(third);
        ReleasedDatabaseTemplate.ValidateClosedEmpty(third);
        Check(QueryOn(third, "SELECT count(*) FROM accounts") is 0L, "副本修改和拒绝分支不污染私有模板");
    }
}
