namespace DfoServer.Persistence;

// 希望任务完成不进入普通永久完成表，领奖以原实例和接取身份独立去重。
internal static class OperationHopeRewardSchema
{
    internal const string Definition = """
        CREATE TABLE character_operation_hope_reward_receipts(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
            acceptance_id TEXT NOT NULL CHECK(length(acceptance_id)=32 AND acceptance_id NOT GLOB '*[^0-9a-f]*' AND acceptance_id<>'00000000000000000000000000000000'),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
            maze_ordinal INTEGER NOT NULL CHECK(maze_ordinal>=0),
            quest_id INTEGER NOT NULL CHECK(quest_id BETWEEN 1 AND 29999),
            clear_epoch INTEGER NOT NULL CHECK(clear_epoch>0),
            generation INTEGER NOT NULL CHECK(generation>0),
            before_revision INTEGER NOT NULL CHECK(before_revision>=0),
            revision INTEGER NOT NULL CHECK(revision=before_revision+1),
            before_level INTEGER NOT NULL CHECK(before_level BETWEEN 1 AND 95),
            after_level INTEGER NOT NULL CHECK(after_level BETWEEN before_level AND 95),
            before_experience INTEGER NOT NULL CHECK(before_experience>=0),
            requested_experience INTEGER NOT NULL CHECK(requested_experience BETWEEN 0 AND 2147483647),
            after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience AND after_experience<=before_experience+requested_experience),
            plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot) AND json_type(plan_snapshot)='object'),
            rewards TEXT NOT NULL CHECK(json_valid(rewards) AND json_type(rewards)='array'),
            changed_items TEXT NOT NULL CHECK(json_valid(changed_items) AND json_type(changed_items)='array'),
            created_at INTEGER NOT NULL CHECK(created_at>=0),
            PRIMARY KEY(character_id,instance_id),
            UNIQUE(character_id,acceptance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;
}
