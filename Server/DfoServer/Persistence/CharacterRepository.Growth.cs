using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // reward必须来自明确业务规则。此入口不生成额度，不接诊断死亡或网络字段。
    // 成功返回后调用方在原owner内确认死亡并发布Selected；这里不修改实例或发送通知。
    public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog,
        FrozenDungeonGroundDeath? loot = null, QuestDropPlan? questDrops = null, DfoServer.Game.Inventory.Services.EquipmentGrowthPlan? equipmentGrowth = null) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(reward);
        ArgumentNullException.ThrowIfNull(catalog);
        if (run.Entry.IsAltar)
        {
            if (loot is not null || questDrops is not null || equipmentGrowth is not null)
                throw new InvalidDataException("祭坛任务死亡不接入普通掉落或装备成长");
            return CommitAltarQuestDeath(owner, expected, run, prepared, reward, catalog);
        }
        ulong amount = reward.Amount;
        if (run.TagEvildomLife is not null || !run.IsPendingDeath(prepared) || prepared.Death.Identity.CharacterId != expected.Id ||
            (run.Entry.DisableMonsterAndClearExperience ? amount != 0 : amount == 0) ||
            string.IsNullOrWhiteSpace(reward.RuleId) || reward.RuleId.Length > 128 || reward.MonsterId != prepared.Monster.MonsterId ||
            reward.MonsterLevel != prepared.Monster.CandidateLevel || reward.ResourceSource != DungeonKillExperienceCatalog.ResourcePath)
            throw new InvalidDataException("成长提交需要本角色当前实例的待提交死亡及符合副本声明的经验");
        var death = prepared.Death;
        var identity = death.Identity;
        if (equipmentGrowth is not null && (!ReferenceEquals(equipmentGrowth.Expected, expected) ||
            equipmentGrowth.EventId != death.EventId || equipmentGrowth.Identity != identity))
            throw new InvalidDataException("装备成长不是原死亡和角色的冻结规划");
        if (questDrops is not null && (!ReferenceEquals(questDrops.Expected,expected) || questDrops.EventId != death.EventId ||
            questDrops.Identity != identity || questDrops.MapId != prepared.Result.Snapshot.CurrentMap.Id))
            throw new InvalidDataException("任务掉落不是原死亡、角色和地图的冻结规划");
        using var db = database.Open();
        // 精确重放只读receipt，不申请writer；新事实升级写事务失败时由原ID恢复。
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonGrowth", deferred: true);
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$event", death.EventId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,amount,
              rule_id,resource_source,monster_id,monster_level,before_experience,after_experience,proficiency_gain
            FROM character_dungeon_growth_receipts WHERE character_id=$id AND event_id=$event
            """;
        bool duplicate;
        uint gained = 0, proficiencyGained = 0;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != owner.Account || reader.GetString(1) != identity.InstanceId.ToString("N") ||
                reader.GetInt64(2) != identity.QuestId || reader.GetInt64(3) != identity.TaskAttempt || reader.GetInt64(4) != identity.Epoch ||
                reader.GetInt64(5) != prepared.Result.Snapshot.CurrentMap.Id || reader.GetInt64(6) != death.MonsterKey ||
                reader.GetInt64(7) != death.CombatSequence || reader.GetInt64(8) != checked((long)amount) ||
                reader.GetString(9) != reward.RuleId || reader.GetString(10) != reward.ResourceSource ||
                reader.GetInt64(11) != reward.MonsterId || reader.GetInt64(12) != reward.MonsterLevel))
                throw new InvalidDataException("同一成长事件ID的来源或奖励被修改");
            if (duplicate)
            {
                gained = checked((uint)(reader.GetInt64(14) - reader.GetInt64(13)));
                proficiencyGained = checked((uint)reader.GetInt64(15));
            }
        }
        var current = duplicate || equipmentGrowth is not null ? Require(db, tx, owner.Account, expected.Name)
            : ReadDungeonGrowthCharacter(db, tx, owner.Account, expected, questDrops);
        if (current.Id != expected.Id)
            throw new InvalidOperationException("成长角色或账号已变化");
        // 同ID重读返回当前持久角色；不根据旧expected再次加经验，也不重发清图事实。
        if (duplicate)
        {
            VerifyFrozenDungeonLoot(db, tx, owner, expected.Id, prepared.Death.EventId, loot);
            CommitOrdinaryLukeDrop(db, tx, owner, run, prepared, loot, verifyOnly: true);
            VerifyQuestDrops(db,tx,owner.Account,expected.Id,prepared.Death.EventId,questDrops);
            current = ObserveMarriageClearInTransaction(db, tx, owner, run, prepared, current, MarriageResources, verifyOnly: true);
            SaveOrVerifyQuestClear(db, tx, owner, run, prepared, current, verifyOnly: true);
            var replayBonuses = ReadCommittedGuildExperience(db, tx, expected.Id, death.EventId, reward.Bonuses);
            tx.Commit();
            // 提交返回丢失时Session仍持有旧装备；仅回放与已落库结果完全一致的成长投影，不再次写资产。
            CacheDungeonLoot(owner, expected, prepared, loot);
            var replayEquipment = equipmentGrowth?.Changes.Where(change =>
                current.Inventory.Find(change.After.Position) == change.After).ToImmutableArray() ?? [];
            return new DungeonGrowthCommit(current, true, gained, proficiencyGained)
                { CommittedBonuses = replayBonuses, EquipmentChanges = replayEquipment };
        }
        var committedBonuses = RevalidateGuildExperience(db, tx, owner.Account, expected.Id, reward.Bonuses,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (committedBonuses is not null) amount = committedBonuses.Total;
        var result = amount == 0 ? CharacterGrowthPlan.WithoutExperience(expected, catalog) : CharacterGrowthPlan.AddExperience(expected, amount, catalog);
        if (!result.Success)
            throw new InvalidDataException("成长候选被拒绝: " + result.Failure);
        var plan = result.Plan!;
        if (current.Revision != plan.Revision || current.Level != plan.PreviousLevel || current.Job != plan.Job ||
            current.GrowType != plan.GrowType || !SameLearning(current.Learning, plan.Before) ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            !QuestCycle.Same(current,expected) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)))
            throw new InvalidOperationException("成长revision或完整学习前提已变化");
        command.Parameters.AddWithValue("$account", owner.Account);
        var inventory = questDrops?.Inventory;
        if (inventory is not null)
            ValidateInventoryPlan(expected, inventory);
        var inventoryPlan = inventory;
        if (equipmentGrowth is not null)
        {
            ValidateInventoryPlan(expected, equipmentGrowth.Inventory);
            if (equipmentGrowth.Inventory.CharacterPrecondition?.IsExpired() == true ||
                inventoryPlan?.CharacterPrecondition?.IsExpired() == true)
                throw new DfoServer.Game.Inventory.Models.InventoryQualificationExpiredException();
            var after = inventoryPlan?.After ?? expected.Inventory;
            foreach (var change in equipmentGrowth.Changes)
            {
                if (after.Find(change.Before.Position) != change.Before || change.After.Uid != change.Before.Uid ||
                    change.After.Position != change.Before.Position)
                    throw new InvalidOperationException("装备成长不能覆盖同笔任务掉落或替换实例身份");
                after = after with { Items = after.Items.Replace(change.Before, change.After) };
            }
            inventoryPlan = new(expected.Inventory, after, "dungeon_equipment_growth", equipmentGrowth.Inventory.CharacterPrecondition);
        }
        var inventoryCharacter = inventoryPlan is null ? current : CommitInventoryInTransaction(db,tx,owner,current,expected,inventoryPlan);
        command.Parameters.AddWithValue("$revision", inventoryCharacter.Revision);
        command.Parameters.AddWithValue("$increment", inventoryPlan?.Changed == true ? 0 : 1);
        command.Parameters.AddWithValue("$level", plan.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
        command.CommandText = """
            UPDATE characters SET level=$level,experience=$experience,revision=revision+$increment
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("成长revision或账号已变化");
        // 自动等级、双树余额和既有主树投影随同一经验事实提交。
        bool learningChanged = !SameLearningSkills(current.Learning, plan.Learning);
        if (learningChanged)
            WriteLearning(db, tx, expected.Id, plan.Learning);
        bool skillsChanged = !current.Skills.SequenceEqual(plan.Learning.Trees.Single(tree => tree.Context == plan.Learning.ActiveContext).Skills
            .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id));
        if (skillsChanged)
            WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        // 副本击杀经验计入冒险团：按角色更新前的有效位置推进里程，满级部分按资源倍率折算。
        if (CreditAdventure(db, tx, owner.Account, expected.Id, AdventureCreditSource.Earned, amount,
                EffectivePosition(current.Level, current.Learning.Experience), current.Level, plan.TargetLevel,
                DateTimeOffset.UtcNow) is { } credit)
            WriteAdventureCredit(db, tx, owner.Account, credit, AdventureCreditSource.Earned, adventureCatalog!);
        // 熟练度与角色经验同源：满级溢出全额、跨过95级入口线只计入口线以上部分。
        // 回执保存这笔经验里真正记入熟练度的额度，重复报告与显示层都按同一有效经验回放。
        if (current.DungeonState is { } dungeonState)
        {
            var proficiencyBefore = new ProficiencyProgress(dungeonState.ProficiencyLevel, dungeonState.ProficiencyExperience);
            var proficiencyGain = ProficiencyProgression.Gain(plan.PreviousLevel, plan.Before.Experience, checked((uint)amount), catalog);
            if (ProficiencyProgression.Advance(proficiencyBefore, proficiencyGain, catalog) is { } proficiency)
            {
                WriteProficiency(db, tx, owner.Account, expected.Id, proficiency.Before, proficiency.After);
                proficiencyGained = checked((uint)proficiency.Credited);
            }
        }
        WriteDungeonGrowthReceipt(db, tx, owner, expected, prepared, reward, plan, proficiencyGained);
        SaveCommittedGuildExperience(db, tx, expected.Id, death.EventId, committedBonuses);
        if (questDrops is not null) SaveQuestDrops(db,tx,owner.Account,questDrops,inventoryCharacter.Inventory);
        CommitOrdinaryLukeDrop(db, tx, owner, run, prepared, loot);
        SaveFrozenDungeonLoot(db, tx, owner, expected, prepared, loot);
        current = ObserveMarriageClearInTransaction(db, tx, owner, run, prepared, current, MarriageResources);
        SaveOrVerifyQuestClear(db, tx, owner, run, prepared, current, verifyOnly: false);
        if (quests is not null)
        {
            var active = prepared.Monster.AiCharacter is null
                ? QuestHuntProgress.MonsterDeath(current.ActiveMainQuests,run.Entry,quests,prepared.Monster)
                : QuestHuntProgress.AiCharacterDeath(current.ActiveMainQuests,run.Entry,quests,prepared.Monster.MonsterId);
            active = QuestHuntProgress.QuestObjectRoomClear(active,run.Entry,quests,prepared.Result.ClearFact);
            if (inventory is not null) active = QuestInventoryProgress.Refresh(active,inventoryCharacter.Inventory,quests,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),QuestEligibility.Completed(current));
            active = QuestStateProgress.Refresh(active,current with { Level = plan.TargetLevel, Learning = plan.Learning,
                Inventory = inventoryCharacter.Inventory },quests);
            WriteQuestConditions(db,tx,expected.Id,current.ActiveMainQuests,active);
        }
        // 复用本事务实读的角色和库存提交结果，只重读成长及任务实际可变的字段。
        // 不能使用调用方旧expected替代current，也不能发布带临时UID的库存规划。
        command.CommandText = "SELECT level,revision FROM characters WHERE id=$id AND account=$account";
        byte committedLevel;
        long committedRevision;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("成长角色或账号已变化");
            committedLevel = checked((byte)reader.GetInt32(0));
            committedRevision = reader.GetInt64(1);
        }
        var committed = inventoryCharacter with
        {
            Marriage = current.Marriage,
            Level = committedLevel,
            Revision = committedRevision,
            Learning = ReadLearning(db, expected.Id, tx),
            Skills = skillsChanged ? ReadCharacterSkills(db, expected.Id, tx) : current.Skills,
            SkillSwitchInventory = skillsChanged ? ReadSkillSwitchInventory(db, expected.Id, tx) : inventoryCharacter.SkillSwitchInventory,
            DungeonState = ReadDungeonState(db, expected.Id, tx),
            ActiveMainQuests = quests is null ? inventoryCharacter.ActiveMainQuests : ReadActiveMainQuests(db, expected.Id, tx)
        };
        if (committed.Level != plan.TargetLevel || committed.Revision != plan.Revision + 1 || !SameLearning(committed.Learning, plan.Learning))
            throw new InvalidOperationException("持久成长结果与候选不一致");
        tx.Commit();
        CacheDungeonLoot(owner, expected, prepared, loot);
        return new DungeonGrowthCommit(committed, false, checked((uint)plan.GainedExperience), proficiencyGained)
            { CommittedBonuses = committedBonuses, EquipmentChanges = equipmentGrowth?.Changes ?? [] };
    });

    static void WriteDungeonGrowthReceipt(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        AccountOwners.Lease owner, Character expected, PreparedDungeonDeath prepared,
        DungeonExperienceReward reward, CharacterGrowthPlan plan, uint proficiencyGained)
    {
        var death = prepared.Death;
        var identity = death.Identity;
        ulong amount = reward.Amount;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", identity.QuestId);
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$map", prepared.Result.Snapshot.CurrentMap.Id);
        command.Parameters.AddWithValue("$key", death.MonsterKey);
        command.Parameters.AddWithValue("$sequence", death.CombatSequence);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$amount", checked((long)amount));
        command.Parameters.AddWithValue("$rule", reward.RuleId);
        command.Parameters.AddWithValue("$resource", reward.ResourceSource);
        command.Parameters.AddWithValue("$monster", reward.MonsterId);
        command.Parameters.AddWithValue("$monsterLevel", reward.MonsterLevel);
        command.Parameters.AddWithValue("$beforeLevel", plan.PreviousLevel);
        command.Parameters.AddWithValue("$afterLevel", plan.TargetLevel);
        command.Parameters.AddWithValue("$beforeExperience", checked((long)plan.Before.Experience));
        command.Parameters.AddWithValue("$afterExperience", checked((long)plan.Learning.Experience));
        command.Parameters.AddWithValue("$revision", checked(plan.Revision + 1));
        command.Parameters.AddWithValue("$proficiencyGained", proficiencyGained);
        command.CommandText = """
            INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,
              rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision,
              proficiency_gain)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$map,$key,$sequence,$generation,$amount,
              $rule,$resource,$monster,$monsterLevel,$beforeLevel,$afterLevel,$beforeExperience,$afterExperience,$revision,
              $proficiencyGained)
            """;
        command.ExecuteNonQuery();
    }
}
