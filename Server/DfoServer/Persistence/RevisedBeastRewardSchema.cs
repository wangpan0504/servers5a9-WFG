namespace DfoServer.Persistence;
internal static class RevisedBeastRewardSchema
{
    // 阶段回执独立去重；awarded仅在首个阶段占用本次实例的领奖额度。
    internal static string StageDefinition => Definition.Replace("character_revised_beast_rewards", "character_revised_beast_stage_rewards", StringComparison.Ordinal);
    internal const string Definition = """
        CREATE TABLE character_revised_beast_rewards(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 5335 AND 5339),
            difficulty INTEGER NOT NULL CHECK(difficulty BETWEEN 0 AND 2),
            success INTEGER NOT NULL CHECK(success IN (0,1)),
            day_key INTEGER NOT NULL,week_key INTEGER NOT NULL,
            awarded INTEGER NOT NULL CHECK(awarded IN (0,1)),
            before_revision INTEGER NOT NULL,revision INTEGER NOT NULL,
            plan_snapshot TEXT NOT NULL CHECK(json_valid(plan_snapshot)),
            PRIMARY KEY(character_id,instance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;
}
