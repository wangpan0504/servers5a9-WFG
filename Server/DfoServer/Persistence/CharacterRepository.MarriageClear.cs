using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Marriage;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    internal MarriageCatalog? MarriageResources { get; set; }
    // 必须在末次死亡成长回执写入后、原任务清图账本之前调用，参与同一个外层事务。
    Character ObserveMarriageClearInTransaction(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, Character current, MarriageCatalog? catalog, bool verifyOnly = false)
    {
        if (prepared.Result.ClearFact is not { } clear || !QuestMarriageProgress.NormalDungeon(run.Entry)) return current;
        var record = ReadMarriage(db, tx, current.Id);
        current = current with { Marriage = record };
        var party = run.MarriagePartyClear;
        bool prepare = party is not null && record is not null;
        bool capsule = record is { State: 4 } && run.Entry.RecommendedQuestClear && catalog is not null;
        if (party is null && !capsule) return current;
        if (!run.IsPendingDeath(prepared) || !QuestClearEligibility.TryValidate(run.Entry, prepared) ||
            current.Id != run.Entry.CharacterId || current.Id != prepared.Death.Identity.CharacterId)
            throw new InvalidDataException("婚姻清图需要当前角色正式实例的可信末次死亡");
        if (party is not null && (record is null || !QuestMarriageProgress.Matches(record, party, clear.Identity.InstanceId)))
            throw new InvalidOperationException("婚姻清图的冻结双方或当前婚约已变化");
        if (!prepare && !capsule) return current;
        // 和任务清图复用同一份成长账本，不能只相信内存中的终末标记。
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT event_id FROM character_dungeon_growth_receipts
            WHERE character_id=$id AND account=$account AND instance_id=$instance AND task_attempt=$attempt
            """;
        query.Parameters.AddWithValue("$id", current.Id);
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$instance", clear.Identity.InstanceId.ToString("N"));
        query.Parameters.AddWithValue("$attempt", clear.Identity.TaskAttempt);
        var deaths = new HashSet<Guid>();
        using (var rows = query.ExecuteReader())
            while (rows.Read()) deaths.Add(Guid.ParseExact(rows.GetString(0), "N"));
        if (!deaths.SetEquals(clear.ConfirmedDeathEvents))
            throw new InvalidDataException("婚姻清图的完整死亡集合未在同一事务提交");
        var before = record!;
        var after = before;
        if (prepare)
        {
            Guid key = QuestMarriageProgress.EventKey(clear.Identity.InstanceId, 0);
            if (!after.ClearEvents.Contains(key))
            {
                if (verifyOnly) throw new InvalidDataException("已提交清图缺少婚约准备计数");
                after = after with { PrepareClears = checked(after.PrepareClears + 1), ClearEvents = after.ClearEvents.Add(key) };
            }
        }
        if (capsule)
        {
            Guid key = QuestMarriageProgress.EventKey(clear.Identity.InstanceId, current.Id);
            if (!after.ClearEvents.Contains(key))
            {
                if (verifyOnly) throw new InvalidDataException("已提交推荐清图缺少本角色胶囊计数");
                var transition = MarriageCoupleRules.ObserveQualifiedClear(after, current.Id, key,
                    QuestCycle.Day(DateTimeOffset.UtcNow), catalog!);
                if (transition.Result != 0) throw new InvalidOperationException("推荐清图胶囊资格已变化");
                after = transition.Record;
            }
        }
        if (!ReferenceEquals(before, after))
        {
            after = after with { Revision = checked(before.Revision + 1) };
            SaveMarriageInTransaction(db, tx, before, after);
        }
        return current with { Marriage = after };
    }
}
