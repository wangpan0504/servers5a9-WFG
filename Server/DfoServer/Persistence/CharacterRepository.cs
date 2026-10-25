using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Persistence;

// 领域存储合同的实现集中在本仓储（邮件、拍卖行挂牌）：它们都要与角色资产共用同一个写事务，
// 因此按合同声明的形状实现，而不是让每个模块各自持有第二个仓储实例（那会变成互相构造）。
// 资源目录由 Runtime 装配后注入：冒险团经验账本与角色成长需要同一份等级经验表与倍率。
internal sealed partial class CharacterRepository(string path, DfoServer.GameWorld.Quests.QuestCatalog? quests = null,
    DfoServer.GameWorld.Adventure.AdventureCatalog? adventureCatalog = null,
    DfoServer.GameWorld.Characters.CharacterGrowthCatalog? growthCatalog = null)
    : ICharacterRepository, DfoServer.Game.Mailbox.IMailboxStore, IAuctionListingStore, DfoServer.Game.Creatures.ICreatureTransportStore
{
    readonly SqliteGameDatabase database = new(path);
    public Character[] List(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM characters WHERE account=$account ORDER BY slot";
        command.Parameters.AddWithValue("$account", owner.Account);
        var rows = new List<Character>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                rows.Add(Read(reader));
        }

        if (rows.Count == 0)
            return [];
        var shared = ReadAccountInventory(db, owner.Account, transaction);
        var accountQuests = ReadAccountQuests(db, rows[0].Id, transaction);
        return rows.Select(character => ReadProgress(db, character, transaction, shared, accountQuests)).ToArray();
    });
    public bool NameExists(string name)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM characters WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! != 0;
    }

    public Character Create(AccountOwners.Lease owner, NewCharacter character) => owner.Execute(() =>
    {
        character.Validate();
        if (!character.Inventory.DailyBoosterUses.IsEmpty)
            throw new InvalidDataException("新角色不能携带历史每日礼盒次数");
        if (!character.Inventory.Skins.Same(SkinStorageState.Empty))
            throw new InvalidDataException("新角色不能携带历史武器外观所有权");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.Create");
        using var transaction = write.Transaction;
        CheckPvpPrivateCreation(db,transaction,owner.Account,character);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT slot FROM characters WHERE account=$account ORDER BY slot";
        command.Parameters.AddWithValue("$account", owner.Account);
        var occupied = new bool[ReadAccountWallet(db, owner.Account, transaction).CharacterSlotCapacity];
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                occupied[reader.GetInt32(0)] = true;
        // 删除不改变其他角色的槽位；连续建角必须复用真正的空槽。
        int slot = Array.FindIndex(occupied, value => !value);
        if (slot < 0)
            throw new InvalidDataException("Character slots full");
        command.CommandText = """
            INSERT INTO characters(account,slot,name,town,area,x,y,direction,
              return_town,return_area,return_x,return_y,return_direction,job,grow_type,level,accepted_story_level,inventory_expansion,gold,experience,equipment_slot_flags)
            VALUES($account,$slot,$name,$town,$area,$x,$y,$direction,
              $rtown,$rarea,$rx,$ry,$rdirection,$job,$grow,$level,$story,$expansion,$gold,$experience,$equipmentSlots) RETURNING id;
            """;
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$name", character.Name);
        command.Parameters.AddWithValue("$job", character.Job);
        command.Parameters.AddWithValue("$grow", character.GrowType);
        command.Parameters.AddWithValue("$equipmentSlots",character.EquipmentSlotFlags);
        command.Parameters.AddWithValue("$level", character.Level);
        command.Parameters.AddWithValue("$story", character.AcceptedStoryLevel);
        command.Parameters.AddWithValue("$experience", checked((long)character.InitialLearning.Experience));
        command.Parameters.AddWithValue("$expansion", character.Inventory.Expansion);
        command.Parameters.AddWithValue("$gold", character.Inventory.Gold);
        Bind(command, "", character.Position);
        Bind(command, "r", character.Return);
        ushort id = checked((ushort)(long)command.ExecuteScalar()!);
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$quest", 0);
        command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id) VALUES($id,$quest)";
        foreach (int quest in character.CompletedQuests)
        {
            command.Parameters["$quest"].Value = quest;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$skill", 0);
        command.Parameters.AddWithValue("$level", 0);
        command.CommandText = "INSERT INTO character_skills VALUES($id,$skill,$level)";
        foreach (var skill in character.Skills)
        {
            command.Parameters["$skill"].Value = skill.Id;
            command.Parameters["$level"].Value = skill.Level;
            command.ExecuteNonQuery();
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$slot", 0);
        command.Parameters.AddWithValue("$quest", 0);
        command.Parameters.AddWithValue("$state", 0);
        command.Parameters.AddWithValue("$acceptance", "");
        var activeQuests = character.ActiveMainQuests.Select(quest => quest with
        {
            AcceptanceId = quest.AcceptanceId == Guid.Empty ? Guid.NewGuid() : quest.AcceptanceId
        }).ToArray();
        command.CommandText = "INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES($id,$slot,$quest,$state,$acceptance)";
        foreach (var quest in activeQuests)
        {
            command.Parameters["$slot"].Value = quest.Slot;
            command.Parameters["$quest"].Value = quest.QuestId;
            command.Parameters["$state"].Value = quest.ConditionState;
            command.Parameters["$acceptance"].Value = quest.AcceptanceId.ToString("N");
            command.ExecuteNonQuery();
        }

        WriteLearning(db, transaction, id, character.InitialLearning);
        CreateDungeonState(db, transaction, owner.Account, id, character.DungeonState);
        CreateTutorial(db, transaction, owner.Account, id, character.Tutorial);
        if (character.DungeonState is not null) CreateFatigue(db, transaction, owner.Account, id);
        // 新角色的初始经验不进冒险团；里程从当前有效位置起算，之后只累计本角色真实获得的经验。
        if (adventureCatalog is not null && growthCatalog is not null)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$position", checked((long)EffectivePosition(character.Level, character.InitialLearning.Experience)));
            command.CommandText = "INSERT INTO character_adventure_credits(character_id,account,baseline_experience,position_experience) VALUES($id,$account,$position,$position) ON CONFLICT(character_id) DO NOTHING";
            command.ExecuteNonQuery();
        }
        var learning = ReadLearning(db, id, transaction);
        var fatigue = ReadFatigue(db, id, transaction);
        // 新角色尚无实体；读取同账号已有仓库后合入角色初始物品，不能重写共享资产。
        var before = ReadInventory(db, id, transaction) with { Gold = 0 };
        var initial = character.Inventory with { AccountWallet = before.AccountWallet, AccountCargo = before.AccountCargo,
            ShopPurchases = before.ShopPurchases, EpicPieces = before.EpicPieces, Items = character.Inventory.Items.AddRange(before.Items) };
        var plan = new InventoryPlan(before, initial, "character_starter");
        var inventory = WriteInventory(db, transaction, owner, id, plan, initial);
        WriteItemMakingSkills(db, transaction, id, owner.Account, plan);
        inventory = WriteWallet(db, transaction, owner, id, plan, inventory);
        inventory = WriteCargo(db, transaction, owner, id, plan, inventory);
        WritePvpPrivateCreation(db,transaction,owner.Account,id,character);
        var accountQuests = ReadAccountQuests(db, id, transaction);
        transaction.Commit();
        return new Character(id, checked((ushort)slot), character.Name, 0, character.Position, character.Return)
        {
            PvpPrivate = character.PvpPrivateCreation is {} privateCreation ? new(privateCreation.RecreateDays) : null,
            Job = character.Job,
            GrowType = character.GrowType,
            Level = character.Level,
            AcceptedStoryLevel = character.AcceptedStoryLevel,
            CompletedQuests = [.. character.CompletedQuests],
            AccountCompletedQuests = accountQuests.Completed,
            AccountQuestAnswers = accountQuests.Answers,
            AccountQuestDailyClaims = accountQuests.Daily,
            Skills = [.. character.Skills],
            ActiveMainQuests = activeQuests,
            Learning = learning,
            Inventory = inventory,
            DungeonState = character.DungeonState,
            Fatigue = fatigue
        };
    });
    public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint, InventoryPlan? inventory = null) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.Save");
        using var transaction = write.Transaction;
        if (inventory is not null) expected = CommitInventoryInTransaction(db, transaction, owner, expected, inventory);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET revision=revision+1,town=$town,area=$area,x=$x,y=$y,direction=$direction,
              return_town=$rtown,return_area=$rarea,return_x=$rx,return_y=$ry,return_direction=$rdirection
            WHERE id=$id AND account=$account AND revision=$revision;
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        Bind(command, "", position);
        Bind(command, "r", returnPoint);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Character revision or owner changed");
        transaction.Commit();
        return expected with
        {
            Revision = expected.Revision + 1,
            Position = position,
            Return = returnPoint
        };
    });
    static void Bind(SqliteCommand command, string prefix, Location? position)
    {
        void Add(string key, object? value) => command.Parameters.AddWithValue("$" + prefix + key, value ?? DBNull.Value);
        Add("town", position?.Town);
        Add("area", position?.Area);
        Add("x", position?.X);
        Add("y", position?.Y);
        Add("direction", position?.Direction);
    }

    static Character Read(SqliteDataReader reader) => new(
        Id: checked((ushort)reader.GetInt32(0)),
        Slot: checked((ushort)reader.GetInt32(2)),
        Name: reader.GetString(3),
        Revision: reader.GetInt64(4),
        Position: new(
            (byte)reader.GetInt32(5),
            (byte)reader.GetInt32(6),
            (ushort)reader.GetInt32(7),
            (ushort)reader.GetInt32(8),
            (byte)reader.GetInt32(9)),
        Return: reader.IsDBNull(10) ? null : new(
            (byte)reader.GetInt32(10),
            (byte)reader.GetInt32(11),
            (ushort)reader.GetInt32(12),
            (ushort)reader.GetInt32(13),
            (byte)reader.GetInt32(14)))
    {
        Job = checked((byte)reader.GetInt32(15)),
        GrowType = checked((byte)reader.GetInt32(16)),
        Level = checked((byte)reader.GetInt32(17)),
        AcceptedStoryLevel = checked((uint)reader.GetInt32(18)),
        QuestCycleDay = reader.GetInt32(reader.GetOrdinal("quest_cycle_day")),
        EquipmentSlotFlags = checked((byte)reader.GetInt32(reader.GetOrdinal("equipment_slot_flags"))),
        Emotion = checked((ushort)reader.GetInt32(reader.GetOrdinal("emotion"))),
        BlacklistCount = reader.GetInt32(reader.GetOrdinal("blacklist_count"))
    };
    static Character ReadProgress(SqliteConnection db, Character character, SqliteTransaction? transaction = null, AccountInventory? shared = null,
        (int[] Completed, System.Collections.Immutable.ImmutableDictionary<int,int> Daily, System.Collections.Immutable.ImmutableDictionary<int,byte> Answers)? accountQuests = null)
    {
        character = ReadQuestProgress(db, character, transaction, accountQuests);
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", character.Id);
        query.CommandText = """
            SELECT COALESCE(g.game_options,a.game_options) FROM characters c
            LEFT JOIN character_game_settings g ON g.character_id=c.id AND g.account=c.account
            LEFT JOIN account_client_settings a ON a.account=c.account
            WHERE c.id=$id
            """;
        byte flags = DfoServer.Game.Settings.ClientSettingsRules.VisibilityFlags(
            query.ExecuteScalar() is byte[] options ? [.. options] : default);
        var expBonus = ReadExpBonus(db, character.Id, transaction);
        var expertJob = ReadExpertJob(db, character.Id, transaction);
        var expertMeta = ReadExpertMeta(db, character.Id, transaction);
        return character with
        {
            VisibilityFlags = flags,
            Skills = ReadCharacterSkills(db, character.Id, transaction),
            Guild = ReadGuildMembership(db, character.Id, transaction),
            ActiveMainQuests = ReadActiveMainQuests(db, character.Id, transaction),
            Learning = ReadLearning(db, character.Id, transaction),
            PermanentStats = ReadPermanentStats(db, character.Id, transaction),
            Inventory = ReadInventory(db, character.Id, transaction, shared),
            SkillSwitchInventory = ReadSkillSwitchInventory(db, character.Id, transaction),
            DungeonState = ReadDungeonState(db, character.Id, transaction),
            Tutorial = ReadTutorial(db, character.Id, transaction),
            Fatigue = ReadFatigue(db, character.Id, transaction),
            TeleportCooldowns = ReadTeleportCooldowns(db, character.Id, transaction),
            ExpBonusUntil = expBonus.Until,
            ExpBonusPermille = expBonus.Permille,
            ExpertJobType = expertJob.Type,
            ExpertJobExperience = expertJob.Experience,
            ExpertJobRecipes = expertJob.Recipes,
            ExpertGiveUpState = expertMeta.GiveUpState,
            ExpertMachineGrade = expertMeta.Grade,
            ExpertMachineEndurance = expertMeta.Endurance,
            TitleProgress = ReadTitleProgress(db, character.Id, transaction),
            TitleSections = ReadTitleSections(db, character.Id, transaction),
            CollectBox = ReadCollectBox(db, character.Id, transaction),
            EpicProduction = ReadEpicProduction(db, character.Id, transaction),
            PvpTrainingSkills = ReadPvpTrainingSkills(db,character.Id,transaction),
            PvpPrivate = ReadPvpPrivate(db,character.Id,transaction)
        };
    }

    static Character ReadQuestProgress(SqliteConnection db, Character character, SqliteTransaction? transaction,
        (int[] Completed, System.Collections.Immutable.ImmutableDictionary<int,int> Daily, System.Collections.Immutable.ImmutableDictionary<int,byte> Answers)? accountQuests = null)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", character.Id);
        query.CommandText = "SELECT quest_id,daily_period,answer FROM character_completed_quests WHERE character_id=$id ORDER BY quest_id";
        var quests = new List<int>();
        var claims = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<int,int>();
        var answers = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<int,byte>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                quests.Add(reader.GetInt32(0));
                if (reader.GetInt32(1) != 0) claims.Add(reader.GetInt32(0), reader.GetInt32(1));
                if (!reader.IsDBNull(2)) answers.Add(reader.GetInt32(0), checked((byte)reader.GetInt32(2)));
            }
        }
        var accountProgress = accountQuests ?? ReadAccountQuests(db, character.Id, transaction);
        return character with
        {
            CompletedQuests = quests.ToArray(), QuestDailyClaims = claims.ToImmutable(), QuestAnswers = answers.ToImmutable(),
            AccountCompletedQuests = accountProgress.Completed, AccountQuestDailyClaims = accountProgress.Daily,
            AccountQuestAnswers = accountProgress.Answers
        };
    }

    static CharacterSkill[] ReadCharacterSkills(SqliteConnection db, ushort id, SqliteTransaction? transaction)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", id);
        query.CommandText = "SELECT skill_id,level FROM character_skills WHERE character_id=$id ORDER BY skill_id";
        using var reader = query.ExecuteReader();
        var skills = new List<CharacterSkill>();
        while (reader.Read())
            skills.Add(new(checked((ushort)reader.GetInt32(0)), checked((byte)reader.GetInt32(1))));
        return skills.ToArray();
    }

    static ActiveMainQuest[] ReadActiveMainQuests(SqliteConnection db, ushort id, SqliteTransaction? transaction)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", id);
        query.CommandText = "SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$id ORDER BY slot";
        using var reader = query.ExecuteReader();
        var active = new List<ActiveMainQuest>();
        while (reader.Read())
            active.Add(new(checked((byte)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), checked((uint)reader.GetInt64(2)))
            {
                AcceptanceId = Guid.ParseExact(reader.GetString(3), "N")
            });
        return active.ToArray();
    }

    static (int[] Completed, System.Collections.Immutable.ImmutableDictionary<int,int> Daily, System.Collections.Immutable.ImmutableDictionary<int,byte> Answers) ReadAccountQuests(SqliteConnection db,
        ushort characterId, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.CommandText = "SELECT q.quest_id,q.daily_period,q.answer FROM account_completed_quests q JOIN characters c ON c.account=q.account WHERE c.id=$id ORDER BY q.quest_id";
        using var reader = command.ExecuteReader();
        var completed = new List<int>(); var daily = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<int,int>();
        var answers = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<int,byte>();
        while (reader.Read())
        {
            int id = reader.GetInt32(0), period = reader.GetInt32(1);
            completed.Add(id); if (period != 0) daily.Add(id, period);
            if (!reader.IsDBNull(2)) answers.Add(id,checked((byte)reader.GetInt32(2)));
        }
        return (completed.ToArray(), daily.ToImmutable(),answers.ToImmutable());
    }
}
