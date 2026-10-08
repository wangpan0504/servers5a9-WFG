using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository(string path) : ICharacterRepository, DfoServer.Game.Mailbox.IMailboxStore
{
    readonly SqliteGameDatabase database = new(path);
    public Character[] List(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction(deferred: true);
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
        return rows.Select(character => ReadProgress(db, character, transaction, shared)).ToArray();
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
        using var db = database.Open();
        using var transaction = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM characters WHERE account=$account";
        command.Parameters.AddWithValue("$account", owner.Account);
        int slot = checked((int)(long)command.ExecuteScalar()!);
        if (slot >= 32)
            throw new InvalidDataException("Character slots full");
        command.CommandText = """
            INSERT INTO characters(account,slot,name,town,area,x,y,direction,
              return_town,return_area,return_x,return_y,return_direction,job,grow_type,level,accepted_story_level,inventory_expansion,gold,experience)
            VALUES($account,$slot,$name,$town,$area,$x,$y,$direction,
              $rtown,$rarea,$rx,$ry,$rdirection,$job,$grow,$level,$story,$expansion,$gold,$experience) RETURNING id;
            """;
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$name", character.Name);
        command.Parameters.AddWithValue("$job", character.Job);
        command.Parameters.AddWithValue("$grow", character.GrowType);
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
        command.CommandText = "INSERT INTO character_completed_quests VALUES($id,$quest)";
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
        if (character.DungeonState is not null)
            CreateFatigue(db, transaction, owner.Account, id);
        var learning = ReadLearning(db, id, transaction);
        // 新角色尚无实体；读取同账号已有仓库后合入角色初始物品，不能重写共享资产。
        var before = ReadInventory(db, id, transaction) with { Gold = 0 };
        var initial = character.Inventory with { AccountWallet = before.AccountWallet, AccountCargo = before.AccountCargo,
            ShopPurchases = before.ShopPurchases, EpicPieces = before.EpicPieces, Items = character.Inventory.Items.AddRange(before.Items) };
        var plan = new InventoryPlan(before, initial, "character_starter");
        var inventory = WriteInventory(db, transaction, owner, id, plan, initial);
        inventory = WriteWallet(db, transaction, owner, id, plan, inventory);
        inventory = WriteCargo(db, transaction, owner, id, plan, inventory);
        transaction.Commit();
        return new Character(id, checked((ushort)slot), character.Name, 0, character.Position, character.Return)
        {
            Job = character.Job,
            GrowType = character.GrowType,
            Level = character.Level,
            AcceptedStoryLevel = character.AcceptedStoryLevel,
            CompletedQuests = [.. character.CompletedQuests],
            Skills = [.. character.Skills],
            ActiveMainQuests = activeQuests,
            Learning = learning,
            Inventory = inventory,
            DungeonState = character.DungeonState,
            Fatigue = character.DungeonState is not null && character.Job == 0 && character.GrowType == 0
                ? CharacterFatigueState.NewOrdinary : null
        };
    });
    public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
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
        AcceptedStoryLevel = checked((uint)reader.GetInt32(18))
    };
    static Character ReadProgress(SqliteConnection db, Character character, SqliteTransaction? transaction = null, AccountInventory? shared = null)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$id", character.Id);
        query.CommandText = "SELECT quest_id FROM character_completed_quests WHERE character_id=$id ORDER BY quest_id";
        var quests = new List<int>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
                quests.Add(reader.GetInt32(0));
        }

        query.CommandText = "SELECT skill_id,level FROM character_skills WHERE character_id=$id ORDER BY skill_id";
        var skills = new List<CharacterSkill>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
                skills.Add(new(checked((ushort)reader.GetInt32(0)), checked((byte)reader.GetInt32(1))));
        }

        query.CommandText = "SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$id ORDER BY slot";
        var active = new List<ActiveMainQuest>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
                active.Add(new(checked((byte)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), checked((uint)reader.GetInt64(2)))
                {
                    AcceptanceId = Guid.ParseExact(reader.GetString(3), "N")
                });
        }

        query.CommandText = """
            SELECT COALESCE(g.game_options,a.game_options) FROM characters c
            LEFT JOIN character_game_settings g ON g.character_id=c.id AND g.account=c.account
            LEFT JOIN account_client_settings a ON a.account=c.account
            WHERE c.id=$id
            """;
        byte flags = DfoServer.Game.Settings.ClientSettingsRules.VisibilityFlags(
            query.ExecuteScalar() is byte[] options ? [.. options] : default);
        return character with
        {
            CompletedQuests = quests.ToArray(),
            VisibilityFlags = flags,
            Skills = skills.ToArray(),
            ActiveMainQuests = active.ToArray(),
            Learning = ReadLearning(db, character.Id, transaction),
            Inventory = ReadInventory(db, character.Id, transaction, shared),
            DungeonState = ReadDungeonState(db, character.Id, transaction),
            Fatigue = ReadFatigue(db, character.Id, transaction)
        };
    }
}
