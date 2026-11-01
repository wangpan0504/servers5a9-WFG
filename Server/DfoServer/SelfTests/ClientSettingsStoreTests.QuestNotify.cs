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
    static void QuestNotifyStorage()
    {
        using var f = new Fixture();
        var entries = SettingsRequests.QuestNotify([3, 2, 0, 0, 0, 47, 117, 0, 0, 3, 1, 0, 0]);
        ImmutableArray<byte> empty = [.. new byte[16]];
        string assets = f.Assets();
        Check(f.Service.Load(f.Owner, f.First.Id).QuestNotify.IsDefault &&
            !f.Service.SaveQuestNotify(f.Owner, f.First.Id, empty) && f.Query("SELECT count(*) FROM character_quest_notify") is 0L,
            "无书签不伪造存档，显式空保存保持无写入");
        Check(f.Service.SaveQuestNotify(f.Owner, f.First.Id, entries), "书签原序提交到当前角色");
        Check(f.Service.Load(f.Owner, f.First.Id).QuestNotify.AsSpan().SequenceEqual(entries.AsSpan()) &&
            f.Service.Load(f.Owner, f.Second.Id).QuestNotify.IsDefault && f.Service.Load(f.OtherOwner, f.Other.Id).QuestNotify.IsDefault &&
            f.Assets() == assets, "书签按角色隔离，不改任务、奖励、资产或revision");
        using (var db = Open(f.Path))
        using (var writer = db.BeginTransaction(deferred: false))
            Check(!f.Service.SaveQuestNotify(f.Owner, f.First.Id, entries), "相同书签不申请writer");
        Reject<InvalidDataException>(() => f.Service.SaveQuestNotify(f.OtherOwner, f.First.Id, empty), "其他账号不可保存此角色书签");
        f.Execute("CREATE TRIGGER fail_notify BEFORE UPDATE ON character_quest_notify BEGIN SELECT RAISE(ABORT,'notify rollback'); END");
        Reject<SqliteException>(() => f.Service.SaveQuestNotify(f.Owner, f.First.Id, empty), "书签SQL失败保留旧值");
        f.Execute("DROP TRIGGER fail_notify");
        var restored = new ClientSettingsService(new ClientSettingsRepository(f.Path)).Load(f.Owner, f.First.Id).QuestNotify;
        Check(restored.AsSpan().SequenceEqual(entries.AsSpan()) && f.Assets() == assets, "重开恢复失败前书签且无资产变化");
        byte[] select = TownPackets.Select(f.First, questNotify: restored);
        // 22B头、30条6B活动任务、一个u32之后，原生选角读取固定四个u32。
        Check(select.Length == 235 && select.AsSpan(206, 16).SequenceEqual(entries.AsSpan()) && select[222] == f.First.Position.Town,
            "选角只填原四书签槽，完整长度和后续城镇字段不移动");
        Check(f.Service.SaveQuestNotify(f.Owner, f.First.Id, empty) &&
            f.Service.Load(f.Owner, f.First.Id).QuestNotify.All(value => value == 0), "取消全部提示可清除旧书签");
        f.Service.SaveQuestNotify(f.Owner, f.First.Id, entries);
        f.Service.SaveQuestNotify(f.Owner, f.Second.Id, entries);
        var characters = new CharacterRepository(f.Path);
        var deletion = characters.LoadDeletionTarget(f.Owner, f.First.Slot);
        f.Execute("CREATE TRIGGER fail_notify_character_delete BEFORE DELETE ON characters BEGIN SELECT RAISE(ABORT,'notify deletion rollback'); END");
        Reject<SqliteException>(() => characters.Delete(f.Owner, deletion), "晚期删角失败回滚已清理书签");
        Check(f.Query("SELECT count(*) FROM character_quest_notify") is 2L &&
            f.Service.Load(f.Owner, f.First.Id).QuestNotify.AsSpan().SequenceEqual(entries.AsSpan()), "删角回滚保留两角色原书签");
        f.Execute("DROP TRIGGER fail_notify_character_delete");
        characters.Delete(f.Owner, deletion);
        Check(f.Query("SELECT count(*) FROM character_quest_notify") is 1L &&
            f.Service.Load(f.Owner, f.Second.Id).QuestNotify.AsSpan().SequenceEqual(entries.AsSpan()), "删角同事务只删除本人书签，其他角色保持");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Service.SaveQuestNotify(f.Owner, f.Second.Id, entries), "旧generation不能覆盖新会话书签");
        f.Integrity();
    }
}
