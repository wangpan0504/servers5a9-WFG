using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    sealed record StoredFrozenDungeonLoot(DungeonRunIdentity Identity, long CombatSequence, long CommittedEpoch,
        DungeonMap Map, ushort MonsterKey, string Resource, bool UsesCollection, ImmutableArray<DungeonGroundObject> Grounds);

    static bool SameFrozenMap(DungeonMap left, DungeonMap right) => left.Kind == right.Kind && left.Position == right.Position &&
        left.Id == right.Id && left.Path == right.Path && left.StoryStageIndex == right.StoryStageIndex &&
        left.LayerIndex == right.LayerIndex && left.DummyStageIndex == right.DummyStageIndex && left.IsConditional == right.IsConditional &&
        left.CandidateIds.SequenceEqual(right.CandidateIds) && left.CandidateWeights.SequenceEqual(right.CandidateWeights);

    static bool SameFrozenGround(DungeonGroundObject left, DungeonGroundObject right) =>
        left.DeathEventId == right.DeathEventId && left.DeathIdentity == right.DeathIdentity && left.Content == right.Content &&
        left.ResourceIdentity == right.ResourceIdentity && left.UsesCollectionLedger == right.UsesCollectionLedger &&
        left.InventoryOrigin is null && right.InventoryOrigin is null && left.PassiveOrigin is null && right.PassiveOrigin is null &&
        left.Reservation.MonsterKey == right.Reservation.MonsterKey && left.Reservation.GroundKey == right.Reservation.GroundKey &&
        left.Reservation.X == right.Reservation.X && left.Reservation.Y == right.Reservation.Y &&
        left.Reservation.RecordSlot == right.Reservation.RecordSlot && left.Reservation.Ordinal == right.Reservation.Ordinal &&
        SameFrozenMap(left.Reservation.Map, right.Reservation.Map);

    static void SaveFrozenDungeonLoot(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, PreparedDungeonDeath prepared, FrozenDungeonGroundDeath? loot)
    {
        if (loot is null) return;
        // 复用原来源、预留编号及单件/集合准入，只在提交明确后发布缓存。
        CacheDungeonLoot(owner, expected, prepared, loot, cache: false);
        if (loot.Grounds.Any(ground => ground.InventoryOrigin is not null || ground.PassiveOrigin is not null ||
            ground.ResourceIdentity != loot.ResourceIdentity || ground.UsesCollectionLedger != loot.UsesCollectionLedger))
            throw new InvalidDataException("死亡冻结对象必须保留父来源及合同，不能借用丢弃或被动物件来源");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", loot.Death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", loot.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", loot.Identity.QuestId);
        command.Parameters.AddWithValue("$attempt", loot.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", loot.Identity.Epoch);
        command.Parameters.AddWithValue("$map", loot.Map.Id);
        command.Parameters.AddWithValue("$monster", loot.Death.MonsterKey);
        command.Parameters.AddWithValue("$resource", loot.ResourceIdentity);
        command.Parameters.AddWithValue("$count", loot.Grounds.Length);
        command.Parameters.AddWithValue("$sequence", loot.Death.CombatSequence);
        command.Parameters.AddWithValue("$committed", loot.CommittedIdentity.Epoch);
        command.Parameters.AddWithValue("$mapSnapshot", JsonSerializer.Serialize(loot.Map));
        command.Parameters.AddWithValue("$collection", loot.UsesCollectionLedger ? 1 : 0);
        command.CommandText = """
            INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,
              epoch,map_id,monster_key,rule_id,resource_source,object_count,freeze_format,combat_sequence,committed_epoch,map_snapshot,uses_collection)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$map,$monster,'frozen-death-v1',$resource,$count,1,$sequence,$committed,$mapSnapshot,$collection)
            """;
        command.ExecuteNonQuery();
        foreach (var ground in loot.Grounds)
        {
            using var item = db.CreateCommand();
            item.Transaction = tx;
            item.CommandText = """
                INSERT INTO character_dungeon_drop_objects(character_id,event_id,instance_id,ordinal,ground_key,core,gold_amount,x,y,record_slot)
                VALUES($id,$event,$instance,$ordinal,$key,$core,$gold,$x,$y,$slot)
                """;
            item.Parameters.AddWithValue("$id", expected.Id);
            item.Parameters.AddWithValue("$event", loot.Death.EventId.ToString("N"));
            item.Parameters.AddWithValue("$instance", loot.Identity.InstanceId.ToString("N"));
            item.Parameters.AddWithValue("$ordinal", ground.Reservation.Ordinal);
            item.Parameters.AddWithValue("$key", ground.Reservation.GroundKey);
            item.Parameters.AddWithValue("$core", ground.IsGold ? DBNull.Value : JsonSerializer.Serialize(ground.Core));
            item.Parameters.AddWithValue("$gold", ground.GoldAmount);
            item.Parameters.AddWithValue("$x", ground.Reservation.X);
            item.Parameters.AddWithValue("$y", ground.Reservation.Y);
            item.Parameters.AddWithValue("$slot", ground.Reservation.RecordSlot);
            item.ExecuteNonQuery();
        }
    }

    static StoredFrozenDungeonLoot? ReadFrozenDungeonLoot(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, ushort characterId, Guid eventId, uint? groundKey = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        command.CommandText = """
            SELECT b.account,b.instance_id,b.quest_id,b.task_attempt,b.epoch,b.map_id,b.monster_key,b.resource_source,
              b.object_count,b.freeze_format,b.combat_sequence,b.committed_epoch,b.map_snapshot,b.uses_collection,
              g.account,g.instance_id,g.quest_id,g.task_attempt,g.epoch,g.map_id,g.monster_key,g.combat_sequence
            FROM character_dungeon_drop_batches b JOIN character_dungeon_growth_receipts g
              ON g.character_id=b.character_id AND g.event_id=b.event_id
            WHERE b.character_id=$id AND b.event_id=$event
            """;
        DungeonRunIdentity identity;
        DungeonMap map;
        ushort monster;
        long sequence, committedEpoch;
        string resource;
        int count;
        bool collection;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetInt32(9) != 1 || reader.IsDBNull(10) || reader.IsDBNull(11) || reader.IsDBNull(12) || reader.IsDBNull(13))
                throw new InvalidDataException("历史掉落格式不能作为当前冻结资格");
            identity = new(characterId, checked((uint)reader.GetInt64(2)), reader.GetInt64(3), Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(4));
            map = JsonSerializer.Deserialize<DungeonMap>(reader.GetString(12)) ?? throw new InvalidDataException("持久冻结缺少完整地图");
            monster = checked((ushort)reader.GetInt64(6));
            resource = reader.GetString(7);
            count = reader.GetInt32(8);
            sequence = reader.GetInt64(10);
            committedEpoch = reader.GetInt64(11);
            collection = reader.GetInt32(13) == 1;
            if (reader.GetString(0) != owner.Account || reader.GetString(14) != owner.Account ||
                reader.GetString(15) != identity.InstanceId.ToString("N") || reader.GetInt64(16) != identity.QuestId ||
                reader.GetInt64(17) != identity.TaskAttempt || reader.GetInt64(18) != identity.Epoch ||
                reader.GetInt64(19) != map.Id || reader.GetInt64(5) != map.Id || reader.GetInt64(20) != monster ||
                reader.GetInt64(21) != sequence || count is < 0 or > 500 || sequence <= 0 || committedEpoch < identity.Epoch ||
                map.CandidateIds is null || map.CandidateWeights is null)
                throw new InvalidDataException("持久冻结与原成长死亡来源不一致");
        }
        command.CommandText = groundKey is null ? """
            SELECT ordinal,ground_key,core,gold_amount,x,y,record_slot,instance_id
            FROM character_dungeon_drop_objects WHERE character_id=$id AND event_id=$event ORDER BY ordinal
            """ : """
            SELECT ordinal,ground_key,core,gold_amount,x,y,record_slot,instance_id
            FROM character_dungeon_drop_objects
            WHERE character_id=$id AND instance_id=$instance AND ground_key=$key AND event_id=$event
            """;
        if (groundKey is { } requestedKey)
        {
            command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            command.Parameters.AddWithValue("$key", requestedKey);
        }
        // 完整死亡只读一次集合；逐件拾取走实例/key索引，不为每件重读整个批次。
        var grounds = ImmutableArray.CreateBuilder<DungeonGroundObject>(groundKey is null ? count : 1);
        var keys = new HashSet<uint>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                int ordinal = reader.GetInt32(0);
                uint key = checked((uint)reader.GetInt64(1));
                var core = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<ItemCore>(reader.GetString(2))
                    ?? throw new InvalidDataException("持久冻结Core缺失");
                var content = new DungeonGroundContent(core, reader.GetInt32(3));
                content.Validate();
                if ((groundKey is null ? ordinal != grounds.Count : ordinal < 0 || ordinal >= count || key != groundKey.Value) ||
                    reader.GetString(7) != identity.InstanceId.ToString("N") || !keys.Add(key))
                    throw new InvalidDataException("持久冻结对象的序号或实例不一致");
                grounds.Add(new(eventId, identity, new(monster, key, map, checked((ushort)reader.GetInt32(4)),
                    checked((ushort)reader.GetInt32(5)), checked((short)reader.GetInt32(6)), ordinal), content)
                    { ResourceIdentity = resource, UsesCollectionLedger = collection });
            }
        }
        if (grounds.Count != (groundKey is null ? count : 1)) throw new InvalidDataException("持久冻结集合或目标对象不完整");
        return new(identity, sequence, committedEpoch, map, monster, resource, collection, grounds.ToImmutable());
    }

    static void VerifyFrozenDungeonLoot(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        ushort characterId, Guid eventId, FrozenDungeonGroundDeath? loot)
    {
        var stored = ReadFrozenDungeonLoot(db, tx, owner, characterId, eventId);
        if (stored is null && loot is null) return;
        if (stored is null || loot is null || stored.Identity != loot.Identity || stored.CombatSequence != loot.Death.CombatSequence ||
            stored.MonsterKey != loot.Death.MonsterKey || loot.Death.EventId != eventId ||
            loot.CommittedIdentity != (stored.Identity with { Epoch = stored.CommittedEpoch }) || !SameFrozenMap(stored.Map, loot.Map) ||
            stored.Resource != loot.ResourceIdentity || stored.UsesCollection != loot.UsesCollectionLedger ||
            stored.Grounds.Length != loot.Grounds.Length || stored.Grounds.Where((ground, i) => !SameFrozenGround(ground, loot.Grounds[i])).Any())
            throw new InvalidDataException("同一死亡的持久冻结身份、完整集合或Core被修改");
    }

    static void VerifyFrozenGround(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        ushort characterId, DungeonGroundObject ground)
    {
        var stored = ReadFrozenDungeonLoot(db, tx, owner, characterId, ground.DeathEventId, ground.Reservation.GroundKey);
        if (stored is null || ground.DeathIdentity.CharacterId != characterId ||
            !stored.Grounds.Any(original => SameFrozenGround(original, ground)))
            throw new InvalidDataException("拾取来源不是原死亡事务的完整持久冻结对象");
    }

    static DungeonPickupCommit? ReadFrozenPickup(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character current, DungeonGroundPickupPreparation prepared, EpicPieceCatalog? pieces = null, ItemCatalog? items = null)
    {
        var ground = prepared.Ground;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT account,event_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,
              after_balance,account_revision,revision,receipt_format,destination_list,epic_piece,account_material
            FROM character_dungeon_drop_pickups WHERE character_id=$id AND instance_id=$instance AND ground_key=$key
            """;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$instance", prepared.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$key", ground.Reservation.GroundKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt32(12) != 1 || reader.GetString(0) != owner.Account ||
            reader.GetString(1) != ground.DeathEventId.ToString("N") || reader.GetInt64(2) != prepared.Identity.TaskAttempt ||
            reader.GetInt64(3) != prepared.Identity.Epoch || prepared.Identity.QuestId != ground.DeathIdentity.QuestId)
            throw new InvalidDataException("持久拾取回执不属于本次完整来源或当前格式");
        long revision = reader.GetInt64(11);
        int amount = reader.GetInt32(8);
        CurrencyKind? currency = reader.IsDBNull(7) ? null : (CurrencyKind)reader.GetInt32(7);
        CurrencyKind? expectedCurrency = ground.IsGold ? CurrencyKind.Gold : CurrencyWallet.RewardKind(ground.Core.ItemId);
        if (currency != expectedCurrency || amount != (ground.IsGold ? ground.GoldAmount : ground.Core.Count))
            throw new InvalidDataException("持久拾取的原数量或币种被修改");
        bool projection = current.Revision == revision;
        InventorySlot destination = new(InventoryListType.Main, 0);
        long uid = 0;
        EpicPiecePickup? epicPiece = null;
        AccountMaterialPickup? accountMaterial = null;
        if (!reader.IsDBNull(15))
        {
            accountMaterial = JsonSerializer.Deserialize<AccountMaterialPickup>(reader.GetString(15))
                ?? throw new InvalidDataException("持久账号材料回执缺少完整余额结果");
            if (ground.IsGold || currency is not null || !reader.IsDBNull(14) || items is null ||
                !items.TryGet(ground.Core.ItemId, out var template) || template.AccountMaterial is null ||
                ground.Core.Kind != ItemKind.Material || amount > template.StackLimit ||
                (ground.Core with { Value = 1 }) != new ItemCore(ground.Core.ItemId, ItemKind.Material, 1) ||
                accountMaterial.ItemId != ground.Core.ItemId || accountMaterial.Amount != amount ||
                accountMaterial.Balance < amount || accountMaterial.Balance > template.AccountMaterial.HoldingLimit ||
                accountMaterial.WalletRevision <= 0)
                throw new InvalidDataException("持久账号材料回执不属于原材料及完整冻结数量");
            projection = projection && current.Inventory.AccountWallet.Revision == accountMaterial.WalletRevision &&
                current.Inventory.AccountWallet.Material(accountMaterial.ItemId).Balance == accountMaterial.Balance;
        }
        else if (!reader.IsDBNull(14))
        {
            epicPiece = JsonSerializer.Deserialize<EpicPiecePickup>(reader.GetString(14))
                ?? throw new InvalidDataException("持久碎片回执缺少完整图鉴结果");
            var definition = !ground.IsGold ? pieces?.ByPiece.GetValueOrDefault(ground.Core.ItemId) : null;
            if (currency is not null || definition is null || epicPiece.Index != definition.Index ||
                epicPiece.PieceId != ground.Core.ItemId || epicPiece.Amount != amount ||
                epicPiece.Balance < amount || epicPiece.BookRevision <= 0)
                throw new InvalidDataException("持久碎片回执不属于原账号图鉴物品与数量");
            projection = projection && current.Inventory.EpicPieces.Revision == epicPiece.BookRevision &&
                current.Inventory.EpicPieces.Get(epicPiece.Index) == epicPiece.Balance;
        }
        else if (currency is { } kind)
            projection = projection && (!CurrencyWallet.IsAccount(kind) || current.Inventory.AccountWallet.Revision == reader.GetInt64(10)) &&
                CurrencyWallet.Get(current.Inventory, kind) == reader.GetInt64(9);
        else
        {
            if (reader.IsDBNull(13) || !Enum.IsDefined((InventoryListType)reader.GetInt32(13)))
                throw new InvalidDataException("持久拾取缺少实际容器");
            destination = new((InventoryListType)reader.GetInt32(13), checked((short)reader.GetInt32(4)));
            uid = reader.GetInt64(5);
            var core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(6)) ?? throw new InvalidDataException("持久拾取Core缺失");
            core.Validate();
            projection = projection && current.Inventory.Find(destination) is { } actual && actual.Uid == uid && actual.Core == core;
        }
        return new(current, true, destination, uid, projection, currency, amount) { EpicPiece = epicPiece, AccountMaterial = accountMaterial };
    }

    static void SaveFrozenPickup(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character committed, DungeonGroundPickupPreparation prepared, InventorySlot destination, long uid, ItemCore? core,
        CurrencyKind? currency, int amount, EpicPiecePickup? epicPiece = null, AccountMaterialPickup? accountMaterial = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
              destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision,receipt_format,destination_list,epic_piece,account_material)
            VALUES($id,$account,$event,$key,$instance,$attempt,$epoch,$slot,$uid,$core,$currency,$amount,$balance,$accountRevision,$revision,1,$list,$epic,$material)
            """;
        command.Parameters.AddWithValue("$id", committed.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", prepared.Ground.DeathEventId.ToString("N"));
        command.Parameters.AddWithValue("$key", prepared.Ground.Reservation.GroundKey);
        command.Parameters.AddWithValue("$instance", prepared.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", prepared.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", prepared.Identity.Epoch);
        bool physical = currency is null && epicPiece is null && accountMaterial is null;
        command.Parameters.AddWithValue("$slot", physical ? destination.Index : DBNull.Value);
        command.Parameters.AddWithValue("$list", physical ? (int)destination.List : DBNull.Value);
        command.Parameters.AddWithValue("$uid", physical ? uid : DBNull.Value);
        command.Parameters.AddWithValue("$core", core is null ? DBNull.Value : JsonSerializer.Serialize(core));
        command.Parameters.AddWithValue("$currency", currency is { } kind ? (int)kind : DBNull.Value);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$balance", currency is { } kindOfBalance ? CurrencyWallet.Get(committed.Inventory, kindOfBalance) : DBNull.Value);
        command.Parameters.AddWithValue("$accountRevision", currency is null ? DBNull.Value : committed.Inventory.AccountWallet.Revision);
        command.Parameters.AddWithValue("$revision", committed.Revision);
        command.Parameters.AddWithValue("$epic", epicPiece is null ? DBNull.Value : JsonSerializer.Serialize(epicPiece));
        command.Parameters.AddWithValue("$material", accountMaterial is null ? DBNull.Value : JsonSerializer.Serialize(accountMaterial));
        command.ExecuteNonQuery();
    }
}
