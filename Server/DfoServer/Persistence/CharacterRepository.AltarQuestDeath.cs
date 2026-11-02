using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    DungeonGrowthCommit CommitAltarQuestDeath(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog growth,
        QuestDropPlan? questDrops)
    {
        if (quests is null || !run.Entry.IsAltar || !run.IsPendingDeath(prepared) ||
            prepared.Death.Identity.CharacterId != expected.Id ||
            reward != DungeonKillExperiencePolicy.PlanAltarQuestDeath(run, prepared))
            throw new InvalidDataException("祭坛任务提交需要原角色死亡和明确的专用零奖励规则");
        if (prepared.BattlePassActivity is { } fact &&
            !fact.MatchesAltarQuestTarget(run,prepared,expected,quests) &&
            !(questDrops?.Outcomes.Length > 0 && fact.MatchesAltarNonTarget(run,prepared,expected,quests)))
            throw new InvalidDataException("祭坛战令来源不是原角色、接取目录与真实死亡的冻结事实");
        var death = prepared.Death;
        var identity = death.Identity;
        if (questDrops is not null && (!ReferenceEquals(questDrops.Expected, expected) || questDrops.EventId != death.EventId ||
            questDrops.Identity != identity || questDrops.MapId != prepared.Result.Snapshot.CurrentMap.Id))
            throw new InvalidDataException("祭坛任务掉落不是原死亡、角色和地图的冻结规划");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitAltarQuestDeath", deferred: true);
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", death.EventId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,
              amount,rule_id,resource_source,monster_id,monster_level,proficiency_gain
            FROM character_dungeon_growth_receipts WHERE character_id=$id AND event_id=$event
            """;
        bool duplicate;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != owner.Account || reader.GetString(1) != identity.InstanceId.ToString("N") ||
                reader.GetInt64(2) != identity.QuestId || reader.GetInt64(3) != identity.TaskAttempt || reader.GetInt64(4) != identity.Epoch ||
                reader.GetInt64(5) != prepared.Result.Snapshot.CurrentMap.Id || reader.GetInt64(6) != death.MonsterKey ||
                reader.GetInt64(7) != death.CombatSequence || reader.GetInt64(8) != 0 || reader.GetString(9) != reward.RuleId ||
                reader.GetString(10) != reward.ResourceSource || reader.GetInt64(11) != reward.MonsterId ||
                reader.GetInt64(12) != reward.MonsterLevel || reader.GetInt64(13) != 0))
                throw new InvalidDataException("祭坛任务死亡回执来源不一致");
        }
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("祭坛任务角色已变化");
        if (duplicate)
        {
            VerifyQuestDrops(db, tx, owner.Account, expected.Id, death.EventId, questDrops);
            tx.Commit();
            return new(current, true);
        }
        if (current.Revision != expected.Revision || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests))
            throw new InvalidOperationException("祭坛任务revision或原接取已变化");
        var inventory = questDrops?.Inventory;
        var inventoryCharacter = inventory is null ? current : CommitInventoryInTransaction(db, tx, owner, current, expected, inventory);
        var active = QuestHuntProgress.MonsterDeath(inventoryCharacter.ActiveMainQuests, run.Entry, quests, prepared.Monster);
        if (inventory is not null)
            active = QuestInventoryProgress.Refresh(active, inventoryCharacter.Inventory, quests,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), QuestEligibility.Completed(current));
        if (active.SequenceEqual(current.ActiveMainQuests) && questDrops?.Outcomes.Length is not > 0)
            throw new InvalidOperationException("祭坛非目标死亡不应申请任务写事务");
        var plan = CharacterGrowthPlan.WithoutExperience(current, growth);
        if (!plan.Success) throw new InvalidDataException("祭坛零经验候选被拒绝: " + plan.Failure);
        command.Parameters.AddWithValue("$revision", inventoryCharacter.Revision);
        command.Parameters.AddWithValue("$increment", inventory?.Changed == true ? 0 : 1);
        command.CommandText = "UPDATE characters SET revision=revision+$increment WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("祭坛任务revision已变化");
        WriteQuestConditions(db, tx, expected.Id, inventoryCharacter.ActiveMainQuests, active);
        WriteDungeonGrowthReceipt(db, tx, owner, current, prepared, reward, plan.Plan!, 0);
        if (questDrops is not null) SaveQuestDrops(db, tx, owner.Account, questDrops, inventoryCharacter.Inventory);
        ApplyBattlePassDungeonDeath(db,tx,owner.Account,run,prepared);
        var committed = inventoryCharacter with { Revision = checked(current.Revision + 1), ActiveMainQuests = ReadActiveMainQuests(db, expected.Id, tx) };
        tx.Commit();
        return new(committed, false);
    }
}
