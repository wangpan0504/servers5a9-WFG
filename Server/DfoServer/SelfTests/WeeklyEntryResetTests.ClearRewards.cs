using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

public static partial class WeeklyEntryResetTests
{
    static void RunClearRewardReset()
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        var period = ClearRewardLimitPolicy.Period(now);
        var limit = new ClearRewardDungeonLimit(1, [291100320, 291100322, 5349], 3, 6);
        var sibling = f.Repository.Create(f.OtherOwner, new NewCharacter("RewardPeer", new(38, 1, 450, 234), null));
        for (int index = 0; index < 6; index++)
            SeedClearReward(f, f.Other.Id, "other", limit.Dungeons[index % 3], index + 1,
                index < 3 ? now : now.AddDays(-1), claimed: index % 2 == 0);
        SeedClearReward(f, sibling.Id, "other", 5349, 10, now);
        SeedClearReward(f, f.Receiver.Id, "receiver", 291100320, 11, now);
        SeedClearReward(f, f.Other.Id, "other", 5349, 12, now, material: false);
        var heroLimit = new ClearRewardDungeonLimit(2, [400000105], 1, 2);
        SeedClearReward(f, f.Other.Id, "other", 400000105, 13, now);
        DungeonIncountUsage Usage(ushort id, ClearRewardDungeonLimit group) =>
            f.Repository.LoadClearRewardLimitUsage(f.OtherOwner, id, group, period);
        That(Usage(f.Other.Id, limit) == new DungeonIncountUsage(3, 6) &&
            !ClearRewardLimitPolicy.CanGrantDungeonReward(limit, Usage(f.Other.Id, limit)),
            "三种泰波尔斯共享日3周6，已领和未领冻结均占次数，空牌不占用");
        string history = ClearRewardHistory(f);
        var result = f.Repository.ResetRewardLimits(f.OtherOwner);
        That(result.ClearRewardRows == 8 && Usage(f.Other.Id, limit) == default && Usage(sibling.Id, limit) == default &&
            Usage(f.Other.Id, heroLimit) == default &&
            f.Repository.LoadClearRewardLimitUsage(f.ReceiverOwner, f.Receiver.Id, limit, period) == new DungeonIncountUsage(1, 1),
            "奖励刷新覆盖原账号全部角色及普通清图限次组，其他账号不变");
        That(ClearRewardHistory(f) == history && ClearRewardLimitPolicy.CanGrantDungeonReward(limit, Usage(f.Other.Id, limit)),
            "刷新恢复下一次材料资格，冻结JSON、领取位、原身份和通关事实逐字保留");
        string saved = f.Snapshot();
        That(f.Repository.ResetRewardLimits(f.OtherOwner).ClearRewardRows == 0 && f.Snapshot() == saved,
            "没有新奖励时重复刷新零新增，整库快照保持");
        _ = new CharacterRepository(f.Path);
        That(Usage(f.Other.Id, limit) == default && f.Snapshot() == saved, "重开仓储保留刷新标记，不复活旧奖励次数");
        SeedClearReward(f, f.Other.Id, "other", 291100322, 14, now);
        That(Usage(f.Other.Id, limit) == new DungeonIncountUsage(1, 1), "刷新后的新实例重新消耗奖励次数");
        // 原未领回执之后完成领取不会覆盖独立标记，也不会使旧次数重新出现。
        f.Execute($"UPDATE character_dungeon_card_rewards SET claimed=1,reward_json=plan_json WHERE character_id={f.Other.Id} AND instance_id='{2:x32}'");
        That(Usage(f.Other.Id, limit) == new DungeonIncountUsage(1, 1), "原未领回执的领取更新不影响新额度");
        var copy = f.Repository.CopyCharacter(f.OtherOwner, new(sibling.Name, sibling.Id, sibling.Revision,
            "RewardCopy", sibling.Position, sibling.Return ?? sibling.Position));
        That(f.Query($"SELECT count(*) FROM character_clear_reward_resets WHERE character_id={copy.Id}") is 0L &&
            f.Query($"SELECT count(*) FROM character_dungeon_card_rewards WHERE character_id={copy.Id}") is 0L,
            "复制角色明确排除原牌面及其刷新标记，不能克隆奖励身份");
        Reject<SqliteException>(() => f.Execute($"INSERT INTO character_clear_reward_resets VALUES({f.Other.Id},'{99:x32}')"),
            "刷新标记必须关联同角色真实奖励回执");
        f.Execute($"DELETE FROM character_dungeon_card_rewards WHERE character_id={f.Other.Id} AND instance_id='{1:x32}'");
        That(f.Query($"SELECT count(*) FROM character_clear_reward_resets WHERE character_id={f.Other.Id} AND instance_id='{1:x32}'") is 0L,
            "原回执删除级联清除排除标记，不遗留可误伤后续实例的水位");
        f.Repository.Delete(f.OtherOwner, f.Repository.LoadDeletionTarget(f.OtherOwner, sibling.Slot));
        That(f.Query($"SELECT count(*) FROM character_clear_reward_resets WHERE character_id={sibling.Id}") is 0L &&
            f.Query("SELECT count(*) FROM character_clear_reward_resets") is 6L, "删角清理自身标记，其他角色标记保留");
        var nextDay = now.AddDays(1);
        var nextWeek = now.AddDays(7);
        DungeonIncountUsage UsageAt(DateTimeOffset at) => f.Repository.LoadClearRewardLimitUsage(f.OtherOwner,
            f.Other.Id, limit, ClearRewardLimitPolicy.Period(at));
        That(UsageAt(nextDay) == new DungeonIncountUsage(0, 1) && UsageAt(nextWeek) == default,
            "刷新后新牌按各自业务周期自然日切和周切，历史标记不复活");
        That(f.Repository.ResetRewardLimits(f.OtherOwner).ClearRewardRows == 1 &&
            UsageAt(now) == default && UsageAt(nextDay) == default,
            "第二次刷新只新增第一轮之后的原实例标记，保留历史排除并恢复日周额度");
        SeedClearReward(f, f.Other.Id, "other", 5349, 15, nextDay);
        SeedClearReward(f, f.Other.Id, "other", 291100320, 16, nextWeek);
        That(UsageAt(nextDay) == new DungeonIncountUsage(1, 1) && UsageAt(nextWeek) == new DungeonIncountUsage(1, 1),
            "多轮刷新后各模式的新回执在所属日周重新计次，不继承旧实例的排除");
        That(f.Repository.ResetRewardLimits(f.OtherOwner).ClearRewardRows == 2 &&
            UsageAt(nextDay) == default && UsageAt(nextWeek) == default,
            "再次刷新覆盖不同创建周期的新回执，旧标记不重复计入新增行数");
        f.Integrity();
    }

    internal static void SeedClearReward(MailboxPersistenceTests.Fixture f, ushort characterId, string account,
        int dungeonId, int serial, DateTimeOffset now, bool claimed = false, bool material = true)
    {
        var card = new DungeonCardReward(0, material ? new ItemCore(10305616, ItemKind.Material, 36) : null,
            TayberrsCardRewardPolicy.Rule, "fixture", now.ToUnixTimeSeconds());
        using var db = f.Open();
        using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$instance", serial.ToString("x32"));
        command.Parameters.AddWithValue("$dungeon", dungeonId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(card));
        command.Parameters.AddWithValue("$claimed", claimed ? 1 : 0);
        command.CommandText = """
            INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
              total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision)
            VALUES($id,$account,$instance,$instance,0,1,1,$dungeon,1,zeroblob(16),x'01',1,0,0,0,0,'fixture','fixture',95,95,0,0,1);
            INSERT INTO character_dungeon_card_rewards(character_id,account,instance_id,plan_json,reward_json,revision,claimed)
            VALUES($id,$account,$instance,$json,$json,1,$claimed);
            """;
        command.ExecuteNonQuery();
    }

    static string ClearRewardHistory(MailboxPersistenceTests.Fixture f)
    {
        using var db = f.Open();
        using var command = db.CreateCommand();
        var rows = new List<object>();
        foreach (string table in new[] { "character_dungeon_settlement_receipts", "character_dungeon_card_rewards", "characters" })
        {
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }
}
