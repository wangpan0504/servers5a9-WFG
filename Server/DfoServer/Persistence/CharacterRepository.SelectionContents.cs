using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public SelectionContentsState ReadSelectionContents(AccountOwners.Lease owner, CharacterRosterEntry? expected,
        DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$id", expected?.Id ?? 0);
        if (expected is not null)
        {
            query.CommandText = "SELECT slot,name FROM characters WHERE id=$id AND account=$account";
            using var identity = query.ExecuteReader();
            if (!identity.Read() || identity.GetInt32(0) != expected.Slot || identity.GetString(1) != expected.Name)
                throw new InvalidOperationException("选角次数查询的名册身份已变化");
        }
        query.CommandText = "SELECT level FROM account_adventures WHERE account=$account";
        int level = Convert.ToInt32(query.ExecuteScalar() ?? 1);
        int day = ShopPurchaseCount.Today(now), gameDay = QuestCycle.Day(now);
        var usage = ReadSpecialEntryUsage(db, tx, owner.Account, expected?.Id ?? 0, day, DungeonSpecialEntryKind.Despair);
        var despair = ReadDespairProgress(db, tx, owner.Account, expected?.Id ?? 0);
        query.CommandText = "SELECT game_day,used FROM account_grief_tower WHERE account=$account";
        int griefUsed = 0;
        using (var reader = query.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetInt32(0) > gameDay) throw new InvalidDataException("悲叹账号计数日期超前");
                if (reader.GetInt32(0) == gameDay) griefUsed = reader.GetInt32(1);
            }
        var grief = expected is null ? new GriefTowerEntryState(gameDay, griefUsed, 0, false, 1) :
            ReadGriefTower(db, tx, owner.Account, expected.Id).State with { AccountDay = gameDay, AccountUsed = griefUsed };
        var counts = ImmutableArray.CreateBuilder<SelectionIncount>();
        var raids = ImmutableArray.CreateBuilder<SelectionRaidClear>();
        if (expected is not null)
        {
            // 入场与追加券各自按原周期读；两表都没有行时无需补空记录或写重置。
            query.CommandText = """
                SELECT k.dungeon,COALESCE(c.day,0),COALESCE(c.daily_used,0),COALESCE(c.week,0),COALESCE(c.weekly_used,0),
                  COALESCE(e.day,0),COALESCE(e.daily_extra,0),COALESCE(e.week,0),COALESCE(e.weekly_extra,0)
                FROM (SELECT dungeon FROM character_dungeon_incount WHERE character_id=$id AND account=$account
                  UNION SELECT dungeon FROM character_dungeon_incount_extra WHERE character_id=$id AND account=$account) k
                LEFT JOIN character_dungeon_incount c ON c.character_id=$id AND c.account=$account AND c.dungeon=k.dungeon
                LEFT JOIN character_dungeon_incount_extra e ON e.character_id=$id AND e.account=$account AND e.dungeon=k.dungeon
                """;
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                {
                    counts.Add(new(reader.GetInt32(0), new(reader.GetInt32(1), reader.GetInt32(3)),
                        new(reader.GetInt32(2), reader.GetInt32(4), reader.GetInt32(6), reader.GetInt32(8)),
                        new(reader.GetInt32(5), reader.GetInt32(7))));
                }
            query.Parameters.AddWithValue("$oldestWeek", QuestCycle.Day(now.AddDays(-8)));
            query.CommandText = """
                SELECT r.raid_type,r.clear_week,count(*) FROM character_raid_runs r
                WHERE r.character_id=$id AND r.account=$account AND r.state=2 AND r.clear_week>=$oldestWeek
                  AND r.entry_epoch=COALESCE((SELECT q.entry_epoch FROM account_raid_quota_epochs q WHERE q.account=r.account AND q.raid_type=r.raid_type),0)
                GROUP BY r.raid_type,r.clear_week
                """;
            using var raidReader = query.ExecuteReader();
            while (raidReader.Read())
                if (raidReader.GetInt32(0) is >= 0 and < 4) raids.Add(new(raidReader.GetInt32(0), raidReader.GetInt32(1), raidReader.GetInt32(2)));
        }
        return new SelectionContentsState(level, usage, despair, grief, counts.ToImmutable(), raids.ToImmutable(), ReadDazzlementTower(db, tx, owner.Account));
    });
}
