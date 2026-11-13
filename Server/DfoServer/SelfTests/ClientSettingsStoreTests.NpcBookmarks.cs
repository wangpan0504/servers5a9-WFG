using System.Collections.Immutable;
using DfoServer.Game.Settings;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class ClientSettingsStoreTests
{
    static void NpcBookmarkStorage()
    {
        NpcBookmarkUpgrade();
        using var f = new Fixture();
        ImmutableArray<byte> entries = [17, 0, 0, 0, 18, 0, 0, 0];
        string assets = f.Assets();
        Check(!f.Service.SaveNpcBookmarks(f.Owner, f.First.Id, []), "空收藏不创建伪存档");
        Check(f.Service.SaveNpcBookmarks(f.Owner, f.First.Id, entries) &&
            f.Service.Load(f.Owner, f.Second.Id).NpcBookmarks.IsDefault &&
            f.Service.Load(f.OtherOwner, f.Other.Id).NpcBookmarks.IsDefault && f.Assets() == assets,
            "人物辞典收藏按角色隔离，不改变好感度或资产");
        using (var db = Open(f.Path))
        using (var writer = db.BeginTransaction(deferred: false))
            Check(!f.Service.SaveNpcBookmarks(f.Owner, f.First.Id, entries), "重复收藏只读返回");
        Reject<InvalidDataException>(() => f.Service.SaveNpcBookmarks(f.OtherOwner, f.First.Id, []), "其他账号不能覆盖收藏");
        Reject<InvalidDataException>(() => ClientSettingsService.ValidateNpcBookmarks([17,0,0,0,17,0,0,0]), "重复收藏编号拒绝");
        Reject<InvalidDataException>(() => SettingsRequests.NpcBookmark([2,17,0,0,0]), "非法收藏操作拒绝");
        Reject<InvalidDataException>(() => SettingsRequests.NpcBookmark([1,17,0,0,0,0]), "收藏请求尾随字节拒绝");
        Check(SettingsRequests.NpcBookmark([1,17,0,0,0]) == (true,17) &&
            SettingsRequests.NpcBookmark([0,17,0,0,0]) == (false,17), "匹配原生单字节操作与32位NPC编号");
        Bytes(SettingsPackets.NpcBookmarks(entries), [2,0,0,0,17,0,0,0,18,0,0,0], "1737独立金样完整收藏列表");
        f.Execute("CREATE TRIGGER fail_bookmark BEFORE UPDATE ON character_npc_bookmarks BEGIN SELECT RAISE(ABORT,'bookmark rollback'); END");
        Reject<SqliteException>(() => f.Service.SaveNpcBookmarks(f.Owner, f.First.Id, []), "收藏SQL失败回滚");
        f.Execute("DROP TRIGGER fail_bookmark");
        var restored = new ClientSettingsService(new ClientSettingsRepository(f.Path)).Load(f.Owner, f.First.Id).NpcBookmarks;
        Check(restored.SequenceEqual(entries), "重开存储恢复失败前收藏");
        var repo = new CharacterRepository(f.Path);
        repo.Delete(f.Owner, repo.LoadDeletionTarget(f.Owner, f.First.Slot));
        Check(f.Query("SELECT count(*) FROM character_npc_bookmarks") is 0L, "删角同事务清理收藏");
        using var replacement = f.Owners.Acquire(f.Owner.Account, static () => { });
        Reject<InvalidOperationException>(() => f.Service.SaveNpcBookmarks(f.Owner, f.Second.Id, entries), "旧owner不能保存收藏");
        f.Integrity();
    }

    static void NpcBookmarkUpgrade()
    {
        using var f = new Fixture();
        var options = Pattern(ClientSettingsService.GameOptionLength, 23);
        f.Service.SaveGameOptions(f.Owner, options);
        string assets = f.Assets(), settings = f.ExistingSettings();
        f.Execute("DROP TABLE character_npc_bookmarks; PRAGMA user_version=200");
        var upgraded = new ClientSettingsRepository(f.Path);
        Check(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
            upgraded.Load(f.Owner, f.First.Id).NpcBookmarks.IsDefault && f.Assets() == assets && f.ExistingSettings() == settings,
            "非空200旧库升级只建空收藏表，资产和已有设置保持");
        Check(upgraded.SaveNpcBookmarks(f.Owner, f.First.Id, [17,0,0,0]) &&
            new ClientSettingsRepository(f.Path).Load(f.Owner, f.First.Id).NpcBookmarks.SequenceEqual(new byte[] {17,0,0,0}),
            "升级后可保存收藏并重开恢复");
        f.Execute("DROP TABLE character_npc_bookmarks; CREATE TABLE character_npc_bookmarks(collision INTEGER); PRAGMA user_version=200");
        Reject<SqliteException>(() => new ClientSettingsRepository(f.Path), "201缺少必需列的收藏结构拒绝");
        Check(f.Query("PRAGMA user_version") is 200L && f.Assets() == assets && f.ExistingSettings() == settings,
            "201失败升级不推进版本或改旧资产设置");
    }
}
