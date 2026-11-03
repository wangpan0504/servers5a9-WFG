using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 108保持历史原文；109只追加类型列和有界两阶段生命周期，不重建既有领奖主键。
internal static class RaidLifecycleSchema
{
    // 每账号只保存当前周两槽；删角不退还绑定名额。
    internal const string FiendwarGuideBindings = """
        CREATE TABLE account_fiendwar_guides(
          account TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 1),
          week INTEGER NOT NULL CHECK(week>0),character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          PRIMARY KEY(account,slot),UNIQUE(account,character_id),
          FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
    // 192之后的当前结构：世代列使GM刷新保留历史绑定，并允许新世代复用两槽。
    internal const string CurrentFiendwarGuideBindings = """
        CREATE TABLE account_fiendwar_guides(
          account TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 1),
          week INTEGER NOT NULL CHECK(week>0),entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          PRIMARY KEY(account,week,entry_epoch,slot),UNIQUE(account,week,entry_epoch,character_id),
          FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
    internal const string TypeColumn = "raid_type INTEGER NOT NULL DEFAULT 0 CHECK(raid_type IN(0,1))";
    internal static string RewardDefinition => RaidRewardSchema.Definition.Split(';')[0]
        .Replace("    PRIMARY KEY(character_id,instance_id,phase,kind)", "    " + TypeColumn + ",\n    PRIMARY KEY(character_id,instance_id,phase,kind)", StringComparison.Ordinal);
    internal const string Definition = """
        CREATE TABLE character_raid_runs(
            character_id INTEGER NOT NULL, account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
            raid_type INTEGER NOT NULL CHECK(raid_type=1),
            source TEXT NOT NULL CHECK(length(source)>0), admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
            state INTEGER NOT NULL CHECK(state IN(0,1,2)),
            phase0_event TEXT, phase0_anchor INTEGER, phase0_receipt TEXT,
            phase1_event TEXT, phase1_anchor INTEGER, phase1_receipt TEXT,
            clear_week INTEGER,
            PRIMARY KEY(character_id,instance_id),
            CHECK((phase0_event IS NULL AND phase0_anchor IS NULL AND phase0_receipt IS NULL) OR
                (phase0_event IS NOT NULL AND phase0_anchor IS NOT NULL AND phase0_receipt IS NOT NULL AND length(phase0_event)=32 AND phase0_anchor>=0 AND json_valid(phase0_receipt) AND json_type(phase0_receipt)='object')),
            CHECK((phase1_event IS NULL AND phase1_anchor IS NULL AND phase1_receipt IS NULL) OR
                (phase1_event IS NOT NULL AND phase1_anchor IS NOT NULL AND phase1_receipt IS NOT NULL AND length(phase1_event)=32 AND phase1_anchor>=0 AND json_valid(phase1_receipt) AND json_type(phase1_receipt)='object')),
            CHECK((state=2 AND clear_week IS NOT NULL AND clear_week>0 AND phase1_event IS NOT NULL) OR (state<>2 AND clear_week IS NULL)),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        )
        """;
    internal static string AntonDefinition => Definition.Replace("CHECK(raid_type=1)", "CHECK(raid_type IN(0,1))", StringComparison.Ordinal);
    internal static string PreyDefinition => AntonDefinition.Replace("IN(0,1)", "IN(0,1,3)", StringComparison.Ordinal);
    internal static string PreyRewardDefinition => RewardDefinition.Replace("CHECK(raid_type IN(0,1))", "CHECK(raid_type IN(0,1,3))", StringComparison.Ordinal);
    internal const string TwoPhaseFinal = "state=2 AND clear_week IS NOT NULL AND clear_week>0 AND phase1_event IS NOT NULL";
    internal const string FiendwarFinal = "state=2 AND clear_week IS NOT NULL AND clear_week>0 AND ((raid_type=2 AND phase0_event IS NOT NULL AND phase1_event IS NULL) OR (raid_type<>2 AND phase1_event IS NOT NULL))";
    internal static string FiendwarDefinition => PreyDefinition.Replace("IN(0,1,3)", "IN(0,1,2,3)", StringComparison.Ordinal)
        .Replace(TwoPhaseFinal, FiendwarFinal, StringComparison.Ordinal);
    internal static string FiendwarRewardDefinition => PreyRewardDefinition.Replace("IN(0,1,3)", "IN(0,1,2,3)", StringComparison.Ordinal);
    internal const string PreyIndexes = "CREATE INDEX character_raid_account_admission ON character_raid_runs(account,raid_type,admitted_at);";
    internal const string Indexes = """
        CREATE UNIQUE INDEX character_raid_active ON character_raid_runs(character_id,raid_type) WHERE state=0;
        CREATE INDEX character_raid_clear_week ON character_raid_runs(character_id,raid_type,clear_week) WHERE state=2;
        CREATE INDEX character_raid_rewards_type_week ON character_raid_reward_receipts(character_id,raid_type,week_key,final_phase,instance_id);
        """;
    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "ALTER TABLE character_raid_reward_receipts ADD COLUMN " + TypeColumn + ";" + Definition + ";" + Indexes;
        command.ExecuteNonQuery();
    }
}
