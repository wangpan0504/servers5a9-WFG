using DfoServer.Game.Settings;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ClientSettingsStoreTests
{
    public static void SeenNoticeStorage()
    {
        using var f = new Fixture();
        Check(f.Service.Load(f.Owner, f.First.Id).SeenNotices.IsEmpty, "新账号不伪造已看提示");
        Check(f.Service.SaveSeenNotice(f.Owner, 42) && !f.Service.SaveSeenNotice(f.Owner, 42), "提示首次提交及重复幂等");
        Check(f.Service.Load(f.Owner, f.Second.Id).SeenNotices.SequenceEqual(new byte[] {42}) &&
            f.Service.Load(f.OtherOwner, f.Other.Id).SeenNotices.IsEmpty, "提示同账号跨角色共享、账号隔离");
        using (var db = Open(f.Path))
        using (var writer = db.BeginTransaction(deferred: false))
            Check(!f.Service.SaveSeenNotice(f.Owner, 42), "重复提示在外部写锁期间只读返回");
        f.Execute("CREATE TRIGGER fail_notice BEFORE INSERT ON account_seen_notices BEGIN SELECT RAISE(ABORT,'notice rollback'); END");
        Reject<SqliteException>(() => f.Service.SaveSeenNotice(f.Owner, 127), "写入失败不保存新提示");
        f.Execute("DROP TRIGGER fail_notice");
        Check(new ClientSettingsRepository(f.Path).Load(f.Owner, f.First.Id).SeenNotices.SequenceEqual(new byte[] {42}), "重开恢复已提交提示及失败前状态");
        Check(f.Service.SaveSeenNotice(f.Owner, 0) && f.Service.SaveSeenNotice(f.Owner, 127), "提示合法端点保存");
        Bytes(SettingsPackets.SeenNotices(f.Service.Load(f.Owner, f.First.Id).SeenNotices), [3,0,42,127], "440独立金样：u8计数及u8编号");
        Bytes(SettingsPackets.SeenNotices(default), [0], "新账号回放空集合");
        Check(SettingsRequests.SeenNotice([42,0,0,0]) == 42, "503按完整u32读取");
        foreach (byte[] body in new byte[][] {[],[42],[42,0,0,0,0],[128,0,0,0],[0,1,0,0]})
            Reject<InvalidDataException>(() => SettingsRequests.SeenNotice(body), "503拒绝截断、尾随及越界");
        Reject<InvalidDataException>(() => f.Service.SaveSeenNotice(f.Owner, 128), "存储前拒绝非法提示编号");
        var repo = new CharacterRepository(f.Path);
        repo.Delete(f.Owner, repo.LoadDeletionTarget(f.Owner, f.First.Slot));
        Check(f.Service.Load(f.Owner, f.Second.Id).SeenNotices.Length == 3, "删角不清除账号提示");
        using var replacement = f.Owners.Acquire(f.Owner.Account, static () => {});
        Reject<InvalidOperationException>(() => f.Service.SaveSeenNotice(f.Owner, 1), "旧generation不能提交提示");
        f.Integrity();
        SeenNoticeUpgrade();
    }

    static void SeenNoticeUpgrade()
    {
        using var f = new Fixture();
        string assets = f.Assets(), settings = f.ExistingSettings();
        f.Execute("DROP TABLE account_seen_notices; PRAGMA user_version=210");
        Check(new ClientSettingsRepository(f.Path).Load(f.Owner, f.First.Id).SeenNotices.IsEmpty &&
            f.Assets() == assets && f.ExistingSettings() == settings, "非空210升级211保留存量资产及设置");
        f.Execute("DROP TABLE account_seen_notices; CREATE TABLE account_seen_notices(collision INTEGER); PRAGMA user_version=210");
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "迁移撞表失败整体回滚");
        Check(f.Query("PRAGMA user_version") is 210L, "失败升级不推进版本");
        f.Execute("DROP TABLE account_seen_notices");
        _ = new ClientSettingsRepository(f.Path);
        f.Execute($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
        Reject<InvalidDataException>(() => new ClientSettingsRepository(f.Path), "较新版本数据库拒绝");
    }
}
